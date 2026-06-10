using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services;

public sealed class CoderConsoleService(
    HttpClient httpClient,
    IConfiguration configuration,
    IWebHostEnvironment environment) : ICoderConsoleService
{
    private const long MaxProjectFileSizeBytes = 200 * 1024;
    private const int MaxSelectedFileCount = 12;
    private const int MaxPromptFileCharacters = 12_000;

    private static readonly HashSet<string> AllowedProjectFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".yml", ".yaml", ".css", ".html", ".js", ".md", ".csproj", ".sln", ".props", ".targets"
    };

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        ".git",
        "Data",
        "node_modules"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public Task<IReadOnlyList<string>> GetWorkspaceRootsAsync()
    {
        var roots = configuration
            .GetSection("AiBox:LocalCoder:WorkspaceRoots")
            .Get<string[]>() ?? [];

        return Task.FromResult<IReadOnlyList<string>>(roots);
    }

    public async Task<IReadOnlyList<string>> GetOllamaModelsAsync()
    {
        var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("/api/tags");
        return response?.Models.Select(x => x.Name).OrderBy(x => x).ToArray() ?? [];
    }

    public Task<IReadOnlyList<ProjectFileItem>> GetProjectFilesAsync(string projectPath)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);
        var files = new List<ProjectFileItem>();

        foreach (var filePath in EnumerateProjectFiles(rootPath))
        {
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.Length > MaxProjectFileSizeBytes)
            {
                continue;
            }

            files.Add(new ProjectFileItem
            {
                RelativePath = Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/'),
                SizeBytes = fileInfo.Length
            });
        }

        return Task.FromResult<IReadOnlyList<ProjectFileItem>>(
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<IReadOnlyList<LocalCoderFileContext>> ReadFileContextsAsync(string projectPath, IReadOnlyList<string> relativePaths)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var contexts = new List<LocalCoderFileContext>();

        foreach (var relativePath in relativePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(MaxSelectedFileCount))
        {
            var normalizedRelativePath = ValidateAndNormalizeSelectedPath(rootPath, relativePath);
            var fullPath = Path.Combine(rootPath, normalizedRelativePath);
            var content = await ReadTextFileAsync(fullPath);

            contexts.Add(new LocalCoderFileContext
            {
                RelativePath = normalizedRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                CharacterCount = content.Length,
                Content = content.Length <= MaxPromptFileCharacters
                    ? content
                    : content[..MaxPromptFileCharacters]
            });
        }

        return contexts;
    }

    public async Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request, AgentModeProfile? profile = null)
    {
        ValidateProjectPath(request.ProjectPath);

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new InvalidOperationException("Task is required.");
        }

        request.FileContexts = (request.FileContexts ?? [])
            .Take(MaxSelectedFileCount)
            .ToList();

        var fileContextText = BuildFileContextText(request.FileContexts);

        var prompt = BuildCreatePlanPrompt(request, fileContextText, profile);

        var ollamaRequest = new OllamaGenerateRequest
        {
            Model = string.IsNullOrWhiteSpace(request.Model)
                ? configuration["AiBox:LocalCoder:DefaultModel"] ?? "qwen2.5-coder:7b"
                : request.Model,
            Prompt = prompt,
            Stream = false
        };

        using var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();

        var task = new ConsoleLocalCoderTask
        {
            ProjectPath = request.ProjectPath,
            Model = ollamaRequest.Model,
            Task = request.Task,
            Plan = result?.Response?.Trim() ?? string.Empty
        };

        await SaveTaskAsync(task);
        return task;
    }

    public async Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request, AgentModeProfile? profile = null)
    {
        ValidateProjectPath(request.ProjectPath);

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new InvalidOperationException("Task is required.");
        }

        if (IsPlanningOnlyTask(request.Task))
        {
            throw new InvalidOperationException("This looks like a planning task. Use Create Plan instead of Patch Preview.");
        }

        request.FileContexts = (request.FileContexts ?? [])
            .Take(MaxSelectedFileCount)
            .ToList();

        if (TryParseExactReplacementTask(request.Task, out var oldText, out var newText))
        {
            var deterministicPatchText = BuildExactReplacementPatch(request.FileContexts, oldText, newText);
            var deterministicValidation = ValidatePatchPreview(deterministicPatchText, request.FileContexts, request.Task);

            if (!deterministicValidation.IsValid)
            {
                throw new InvalidOperationException($"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", deterministicValidation.Errors)}");
            }

            return new LocalCoderPatchPreview
            {
                ProjectPath = request.ProjectPath,
                Model = "deterministic-exact-replacement",
                Task = request.Task,
                FileContexts = request.FileContexts.ToArray(),
                PatchText = deterministicPatchText
            };
        }

        var fileContextText = BuildPatchPreviewFileContextText(request.FileContexts);
        var selectedFilePathsText = BuildSelectedFilePathsText(request.FileContexts);

        var prompt = BuildGeneratePatchPreviewPrompt(request, selectedFilePathsText, fileContextText, profile);

        var ollamaRequest = new OllamaGenerateRequest
        {
            Model = string.IsNullOrWhiteSpace(request.Model)
                ? configuration["AiBox:LocalCoder:DefaultModel"] ?? "qwen2.5-coder:7b"
                : request.Model,
            Prompt = prompt,
            Stream = false
        };

        using var response = await httpClient.PostAsJsonAsync("/api/generate", ollamaRequest);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        var patchText = CleanPatchPreviewText(result?.Response ?? string.Empty);
        var validation = ValidatePatchPreview(patchText, request.FileContexts, request.Task);

        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", validation.Errors)}");
        }

        return new LocalCoderPatchPreview
        {
            ProjectPath = request.ProjectPath,
            Model = ollamaRequest.Model,
            Task = request.Task,
            FileContexts = request.FileContexts.ToArray(),
            PatchText = patchText
        };
    }

    public async Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview)
    {
        ArgumentNullException.ThrowIfNull(patchPreview);

        var rootPath = GetValidatedProjectRoot(patchPreview.ProjectPath);
        var patchText = CleanPatchPreviewText(patchPreview.PatchText);

        if (patchText.Contains("PATCH_NOT_POSSIBLE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PATCH_NOT_POSSIBLE cannot be applied.");
        }

        if (!patchText.StartsWith("diff --git", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Patch apply requires PatchText that starts with diff --git.");
        }

        var validation = ValidatePatchPreview(patchText, patchPreview.FileContexts, patchPreview.Task);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"Patch apply validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", validation.Errors)}");
        }

        var selectedPaths = new HashSet<string>(
            patchPreview.FileContexts.Select(fileContext => NormalizePreviewPath(fileContext.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        var changedFiles = ExtractChangedPathsFromDiffHeaders(patchText.ReplaceLineEndings("\n").Split('\n'))
            .Select(NormalizePreviewPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && !IsDevNullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (changedFiles.Length == 0)
        {
            throw new InvalidOperationException("Patch does not contain any changed file paths.");
        }

        foreach (var changedFile in changedFiles)
        {
            if (Path.IsPathRooted(changedFile) ||
                changedFile.StartsWith("/", StringComparison.Ordinal) ||
                changedFile.Contains("..", StringComparison.Ordinal) ||
                !selectedPaths.Contains(changedFile))
            {
                throw new InvalidOperationException($"Patch apply references a file not present in selected file context: {changedFile}");
            }
        }

        var tempPatchPath = Path.Combine(Path.GetTempPath(), $"aibox-local-coder-{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(tempPatchPath, patchText + Environment.NewLine);

        try
        {
            var backupRoot = CreatePatchBackupDirectory(rootPath);
            var backupFiles = new List<string>();
            var originalFileContents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var changedFile in changedFiles)
            {
                var normalizedRelativePath = ValidateAndNormalizeSelectedPath(rootPath, changedFile);
                var sourcePath = Path.Combine(rootPath, normalizedRelativePath);
                var backupPath = Path.Combine(backupRoot, normalizedRelativePath);

                originalFileContents[normalizedRelativePath] = await File.ReadAllBytesAsync(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(sourcePath, backupPath, overwrite: false);
                backupFiles.Add(Path.GetRelativePath(rootPath, backupPath).Replace('\\', '/'));
            }

            var applyResult = await RunFixedCommandAsync(
                rootPath,
                "git",
                ["apply", "--whitespace=fix", tempPatchPath],
                $"git apply --whitespace=fix {tempPatchPath}");

            if (applyResult.ExitCode != 0)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = $"Patch apply failed after backups were created: {GetCommandFailureMessage(applyResult)}",
                    GitApplyResult = applyResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = []
                };
            }

            var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");

            if (diffStatResult.ExitCode != 0)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = $"Patch apply failed: could not verify repository changes. {GetCommandFailureMessage(diffStatResult)}",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            if (string.IsNullOrWhiteSpace(diffStatResult.Output))
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = "Patch apply failed: no repository changes detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            var changedTargetProven = false;
            foreach (var changedFile in changedFiles)
            {
                var normalizedRelativePath = ValidateAndNormalizeSelectedPath(rootPath, changedFile);
                var currentContent = await File.ReadAllBytesAsync(Path.Combine(rootPath, normalizedRelativePath));

                if (!currentContent.AsSpan().SequenceEqual(originalFileContents[normalizedRelativePath]))
                {
                    changedTargetProven = true;
                    break;
                }
            }

            if (!changedTargetProven)
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = "Patch apply failed: no selected file changes detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult]
                };
            }

            var changedFilesDiffArguments = new List<string> { "diff", "--" };
            changedFilesDiffArguments.AddRange(changedFiles);
            var changedFilesDiffResult = await RunFixedCommandAsync(
                rootPath,
                "git",
                changedFilesDiffArguments,
                $"git diff -- {string.Join(' ', changedFiles)}");

            if (changedFilesDiffResult.ExitCode != 0 || string.IsNullOrWhiteSpace(changedFilesDiffResult.Output))
            {
                return new LocalCoderPatchApplyResult
                {
                    Applied = false,
                    VerificationPassed = false,
                    Message = changedFilesDiffResult.ExitCode != 0
                        ? $"Patch apply failed: could not verify changed file diff. {GetCommandFailureMessage(changedFilesDiffResult)}"
                        : "Patch apply failed: no changed file diff detected after git apply.",
                    GitApplyResult = applyResult,
                    GitDiffStatResult = diffStatResult,
                    ChangedFilesDiffResult = changedFilesDiffResult,
                    ChangedFiles = changedFiles,
                    BackupFiles = backupFiles,
                    VerificationResults = [diffStatResult, changedFilesDiffResult]
                };
            }

            var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
            var testResult = await RunFixedCommandAsync(rootPath, "dotnet", ["test"], "dotnet test");
            var verificationPassed = buildResult.ExitCode == 0 && testResult.ExitCode == 0;

            return new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = verificationPassed,
                Message = verificationPassed
                    ? $"Patch applied to {rootPath} and verification passed."
                    : $"Patch applied to {rootPath}, but build or test verification failed. Review the verification results and backups.",
                GitApplyResult = applyResult,
                GitDiffStatResult = diffStatResult,
                ChangedFilesDiffResult = changedFilesDiffResult,
                ChangedFiles = changedFiles,
                BackupFiles = backupFiles,
                VerificationResults = [diffStatResult, changedFilesDiffResult, buildResult, testResult]
            };
        }
        finally
        {
            File.Delete(tempPatchPath);
        }
    }

    public async Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(
        LocalCoderPatchApplyResult applyResult,
        string projectPath)
    {
        ArgumentNullException.ThrowIfNull(applyResult);

        var rootPath = GetValidatedProjectRoot(projectPath);

        if (applyResult.BackupFiles.Count == 0)
        {
            throw new InvalidOperationException("Rollback requires backup files from a previous apply.");
        }

        var restoredFiles = new List<string>();

        foreach (var backupFile in applyResult.BackupFiles)
        {
            if (string.IsNullOrWhiteSpace(backupFile))
            {
                continue;
            }

            var backupRelativePath = backupFile.Replace('\\', '/').Trim();
            var backupFullPath = Path.GetFullPath(Path.Combine(rootPath, backupRelativePath));
            var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            if (!backupFullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Backup file is outside the project root: {backupFile}");
            }

            if (!File.Exists(backupFullPath))
            {
                throw new InvalidOperationException($"Backup file does not exist: {backupFile}");
            }

            var restoredRelativePath = GetRestoredRelativePathFromBackup(backupRelativePath);
            var restoredFullPath = Path.GetFullPath(Path.Combine(rootPath, restoredRelativePath));

            if (!restoredFullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Restored path is outside the project root: {restoredRelativePath}");
            }

            var restoredDirectory = Path.GetDirectoryName(restoredFullPath);
            if (!string.IsNullOrWhiteSpace(restoredDirectory))
            {
                Directory.CreateDirectory(restoredDirectory);
            }

            File.Copy(backupFullPath, restoredFullPath, overwrite: true);
            restoredFiles.Add(restoredRelativePath.Replace('\\', '/'));
        }

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        var testResult = await RunFixedCommandAsync(rootPath, "dotnet", ["test"], "dotnet test");

        var verificationResults = new[] { diffStatResult, buildResult, testResult };
        var verificationPassed = verificationResults.All(result => result.ExitCode == 0);

        return new LocalCoderPatchRollbackResult
        {
            RolledBack = true,
            VerificationPassed = verificationPassed,
            Message = verificationPassed
                ? $"Rollback restored {restoredFiles.Count} file(s) and verification passed."
                : $"Rollback restored {restoredFiles.Count} file(s), but verification failed. Review the verification results.",
            RestoredFiles = restoredFiles,
            VerificationResults = verificationResults
        };
    }

    public async Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            throw new InvalidOperationException("Commit message is required.");
        }

        var results = new List<CommandRunResult>();

        var statusResult = await RunFixedCommandAsync(rootPath, "git", ["status", "--short"], "git status --short");
        results.Add(statusResult);

        if (statusResult.ExitCode != 0 || string.IsNullOrEmpty(statusResult.Output))
        {
            return results;
        }

        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        results.Add(buildResult);

        if (buildResult.ExitCode != 0)
        {
            return results;
        }

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        results.Add(diffStatResult);

        var addResult = await RunFixedCommandAsync(rootPath, "git", ["add", "."], "git add .");
        results.Add(addResult);

        if (addResult.ExitCode != 0)
        {
            return results;
        }

        var commitResult = await RunFixedCommandAsync(
            rootPath,
            "git",
            ["commit", "-m", commitMessage],
            "git commit -m <commit message>");
        results.Add(commitResult);

        if (commitResult.ExitCode != 0)
        {
            return results;
        }

        var deployResult = await RunFixedCommandAsync(
            rootPath,
            "docker",
            ["compose", "up", "-d", "--build", "aibox-devportal"],
            "docker compose up -d --build aibox-devportal");
        results.Add(deployResult);

        return results;
    }

    public async Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath)
    {
        var rootPath = GetValidatedProjectRoot(projectPath);

        var results = new List<CommandRunResult>();

        var buildResult = await RunFixedCommandAsync(rootPath, "dotnet", ["build"], "dotnet build");
        results.Add(buildResult);

        if (buildResult.ExitCode != 0)
        {
            return results;
        }

        var statusResult = await RunFixedCommandAsync(rootPath, "git", ["status", "--short"], "git status --short");
        results.Add(statusResult);

        var diffStatResult = await RunFixedCommandAsync(rootPath, "git", ["diff", "--stat"], "git diff --stat");
        results.Add(diffStatResult);

        var deployResult = await RunFixedCommandAsync(
            rootPath,
            "docker",
            ["compose", "up", "-d", "--build", "aibox-devportal"],
            "docker compose up -d --build aibox-devportal");
        results.Add(deployResult);

        return results;
    }

    public async Task<CommandRunResult> RunCommandAsync(string projectPath, string command)
    {
        ValidateProjectPath(projectPath);
        ValidateCommand(command);

        var result = new CommandRunResult
        {
            Command = command,
            Started = DateTimeOffset.UtcNow
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-lc \"{EscapeBash(command)}\"",
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Could not start process.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            result.ExitCode = -1;
            result.Error = "Command timed out after 5 minutes.";
            result.Finished = DateTimeOffset.UtcNow;
            return result;
        }

        result.Output = await outputTask;
        result.Error = await errorTask;
        result.ExitCode = process.ExitCode;
        result.Finished = DateTimeOffset.UtcNow;

        return result;
    }

    private static async Task<CommandRunResult> RunFixedCommandAsync(
        string projectPath,
        string executable,
        IReadOnlyList<string> arguments,
        string displayCommand)
    {
        var result = new CommandRunResult
        {
            Command = displayCommand,
            Started = DateTimeOffset.UtcNow
        };

        var processStartInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"Could not start fixed command: {displayCommand}");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            result.ExitCode = -1;
            result.Error = "Command timed out after 10 minutes.";
            result.Finished = DateTimeOffset.UtcNow;
            return result;
        }

        result.Output = await outputTask;
        result.Error = await errorTask;
        result.ExitCode = process.ExitCode;
        result.Finished = DateTimeOffset.UtcNow;
        return result;
    }

    public async Task<IReadOnlyList<ConsoleLocalCoderTask>> GetHistoryAsync()
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();

            if (!File.Exists(path))
            {
                return [];
            }

            await using var stream = File.OpenRead(path);
            var tasks = await JsonSerializer.DeserializeAsync<List<ConsoleLocalCoderTask>>(stream, JsonOptions);
            return tasks?.OrderByDescending(x => x.Created).ToArray() ?? [];
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task SaveTaskAsync(ConsoleLocalCoderTask task)
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tasks = new List<ConsoleLocalCoderTask>();

            if (File.Exists(path))
            {
                await using var readStream = File.OpenRead(path);
                tasks = await JsonSerializer.DeserializeAsync<List<ConsoleLocalCoderTask>>(readStream, JsonOptions) ?? [];
            }

            tasks.Add(task);

            await using var writeStream = File.Create(path);
            await JsonSerializer.SerializeAsync(writeStream, tasks, JsonOptions);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private void ValidateProjectPath(string projectPath)
    {
        _ = GetValidatedProjectRoot(projectPath);
    }

    private void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Command is required.");
        }

        var allowedCommands = configuration
            .GetSection("AiBox:LocalCoder:AllowedCommands")
            .Get<string[]>() ?? [];

        var allowed = allowedCommands.Any(allowed =>
            command.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith(allowed + " ", StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            throw new InvalidOperationException($"Command is not allowed: {command}");
        }

        string[] blocked =
        [
            " rm ",
            " sudo ",
            " chmod ",
            " chown ",
            " docker ",
            " mkfs ",
            " dd ",
            " shutdown ",
            " reboot ",
            " curl ",
            " wget ",
            " > ",
            " >> "
        ];

        var padded = $" {command} ";

        if (blocked.Any(x => padded.Contains(x, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Command contains blocked token: {command}");
        }
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "coder-tasks.json");
    }

    private static string CreatePatchBackupDirectory(string projectPath)
    {
        var backupRoot = Path.Combine(
            projectPath,
            "Data",
            "patch-backups",
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));

        var candidate = backupRoot;
        var suffix = 1;

        while (Directory.Exists(candidate))
        {
            candidate = $"{backupRoot}-{suffix:00}";
            suffix++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string GetCommandFailureMessage(CommandRunResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error)
            ? result.Output
            : result.Error;

        return $"{result.Command} exited {result.ExitCode}: {detail.Trim()}";
    }

    private static string GetRestoredRelativePathFromBackup(string backupRelativePath)
    {
        const string backupPrefix = "Data/patch-backups/";
        var normalizedBackupPath = backupRelativePath.Replace('\\', '/').Trim();

        if (!normalizedBackupPath.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Backup file is not under the expected backup folder: {backupRelativePath}");
        }

        var remainder = normalizedBackupPath[backupPrefix.Length..];
        var firstSeparator = remainder.IndexOf('/');

        if (firstSeparator < 0 || firstSeparator == remainder.Length - 1)
        {
            throw new InvalidOperationException($"Backup file path does not include a restorable file location: {backupRelativePath}");
        }

        return remainder[(firstSeparator + 1)..];
    }

    internal static string BuildCreatePlanPrompt(
        LocalCoderRequest request,
        string fileContextText,
        AgentModeProfile? profile = null)
    {
        var profileText = BuildAgentModeProfileText(profile);
        return $$"""
        You are a local coding assistant for a C# Blazor/Radzen project.

        {{profileText}}

        Project path:
        {{request.ProjectPath}}

        User task:
        {{request.Task}}

        Selected file count: {{request.FileContexts.Count}}

        Selected file context:
        {{fileContextText}}

        Produce:
        1. Short diagnosis.
        2. Files likely involved.
        3. Step-by-step implementation plan.
        4. Safe commands to verify.
        5. Do not invent files if unsure.
        6. Do not suggest destructive commands.

        Return plain markdown.
        """;
    }

    internal static string BuildGeneratePatchPreviewPrompt(
        LocalCoderRequest request,
        string selectedFilePathsText,
        string fileContextText,
        AgentModeProfile? profile = null)
    {
        var profileText = BuildAgentModeProfileText(profile);
        return $$"""
        You are a coding assistant generating a patch preview for a C# Blazor/Radzen project.

        Return only the diff.
        The diff must be a unified diff that starts with diff --git.
        Do not return a plan.
        Do not return markdown explanation.
        Do not return headings.
        Only modify files from the selected file context.
        Use repository-relative paths.
        If no concrete patch can be created, return PATCH_NOT_POSSIBLE.

        {{profileText}}

        Project path:
        {{request.ProjectPath}}

        Task:
        {{request.Task}}

        Selected file count: {{request.FileContexts.Count}}

        Selected file paths:
        {{selectedFilePathsText}}

        Selected file context:
        {{fileContextText}}

        Generate the smallest safe patch preview possible.
        """;
    }

    internal static string BuildAgentModeProfileText(AgentModeProfile? profile)
    {
        if (profile is null)
        {
            return string.Empty;
        }

        return $"""
        Agent mode profile:
        Name: {profile.Name}
        Mode: {profile.Mode}
        Model: {profile.Model}
        Rules summary: {profile.RulesSummary}
        """;
    }

    private string GetValidatedProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("Project path is required.");
        }

        var fullPath = Path.GetFullPath(projectPath);

        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Project path does not exist: {fullPath}");
        }

        var roots = configuration
            .GetSection("AiBox:LocalCoder:WorkspaceRoots")
            .Get<string[]>() ?? [];

        var allowed = roots
            .Select(Path.GetFullPath)
            .Any(root => fullPath.StartsWith(root, StringComparison.Ordinal));

        if (!allowed)
        {
            throw new InvalidOperationException("Project path is outside allowed workspace roots.");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string rootPath)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentDirectory = stack.Pop();

            foreach (var filePath in Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var fileInfo = new FileInfo(filePath);

                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (IsAllowedProjectFile(filePath))
                {
                    yield return filePath;
                }
            }

            foreach (var directoryPath in Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var directoryInfo = new DirectoryInfo(directoryPath);

                if (directoryInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (!IgnoredDirectoryNames.Contains(directoryInfo.Name))
                {
                    stack.Push(directoryPath);
                }
            }
        }
    }

    private static bool IsAllowedProjectFile(string filePath)
    {
        return AllowedProjectFileExtensions.Contains(Path.GetExtension(filePath));
    }

    private static string ValidateAndNormalizeSelectedPath(string rootPath, string relativePath)
    {
        var trimmed = relativePath.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            throw new InvalidOperationException("Selected file paths must be repository-relative.");
        }

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' must not contain a drive separator.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' cannot contain '..'.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, trimmed));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' is outside the project root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' does not exist.");
        }

        if (!IsAllowedProjectFile(fullPath))
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' is not an allowed source file.");
        }

        var fileInfo = new FileInfo(fullPath);

        if (fileInfo.Length > MaxProjectFileSizeBytes)
        {
            throw new InvalidOperationException($"Selected file path '{trimmed}' exceeds the {MaxProjectFileSizeBytes / 1024} KB limit.");
        }

        return Path.GetRelativePath(rootPath, fullPath);
    }

    private static async Task<string> ReadTextFileAsync(string fullPath)
    {
        await using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync();
        return content;
    }

    private static string BuildFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided.";
        }

        var builder = new System.Text.StringBuilder();

        foreach (var fileContext in fileContexts.Take(MaxSelectedFileCount))
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, MaxPromptFileCharacters));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildPatchPreviewFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided. No files are currently loaded.";
        }

        var builder = new System.Text.StringBuilder();

        foreach (var fileContext in fileContexts.Take(MaxSelectedFileCount))
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, MaxPromptFileCharacters));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildSelectedFilePathsText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file paths were provided.";
        }

        return string.Join(Environment.NewLine, fileContexts.Select(fileContext => $"- {fileContext.RelativePath}"));
    }

    private static bool TryParseExactReplacementTask(string task, out string oldText, out string newText)
    {
        var match = Regex.Match(
            task,
            "replace\\s+this\\s+exact\\s+text:\\s*\"(?<old>.*?)\"\\s*with:\\s*\"(?<new>.*?)\"",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            oldText = string.Empty;
            newText = string.Empty;
            return false;
        }

        oldText = match.Groups["old"].Value;
        newText = match.Groups["new"].Value;
        return true;
    }

    private static string BuildExactReplacementPatch(
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string oldText,
        string newText)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            throw new InvalidOperationException("Old text must not be empty.");
        }

        var matches = fileContexts
            .Select(fileContext => new
            {
                FileContext = fileContext,
                MatchCount = CountExactOccurrences(fileContext.Content, oldText)
            })
            .Where(match => match.MatchCount > 0)
            .ToArray();

        var totalMatchCount = matches.Sum(match => match.MatchCount);
        if (totalMatchCount == 0)
        {
            throw new InvalidOperationException("Old text was not found in selected files.");
        }

        if (totalMatchCount > 1)
        {
            throw new InvalidOperationException("Old text appears multiple times; make the task more specific.");
        }

        var fileContext = matches.Single().FileContext;
        var relativePath = NormalizePreviewPath(fileContext.RelativePath);
        var updatedContent = fileContext.Content.Replace(oldText, newText, StringComparison.Ordinal);
        var oldLines = fileContext.Content.ReplaceLineEndings("\n").Split('\n');
        var newLines = updatedContent.ReplaceLineEndings("\n").Split('\n');

        var commonPrefixCount = 0;
        while (commonPrefixCount < oldLines.Length &&
               commonPrefixCount < newLines.Length &&
               string.Equals(oldLines[commonPrefixCount], newLines[commonPrefixCount], StringComparison.Ordinal))
        {
            commonPrefixCount++;
        }

        var commonSuffixCount = 0;
        while (commonSuffixCount < oldLines.Length - commonPrefixCount &&
               commonSuffixCount < newLines.Length - commonPrefixCount &&
               string.Equals(
                   oldLines[oldLines.Length - commonSuffixCount - 1],
                   newLines[newLines.Length - commonSuffixCount - 1],
                   StringComparison.Ordinal))
        {
            commonSuffixCount++;
        }

        const int contextLineCount = 3;
        var hunkStartIndex = Math.Max(0, commonPrefixCount - contextLineCount);
        var oldChangedEndIndex = oldLines.Length - commonSuffixCount;
        var newChangedEndIndex = newLines.Length - commonSuffixCount;
        var oldHunkEndIndex = Math.Min(oldLines.Length, oldChangedEndIndex + contextLineCount);
        var newHunkEndIndex = Math.Min(newLines.Length, newChangedEndIndex + contextLineCount);
        var oldHunkLineCount = oldHunkEndIndex - hunkStartIndex;
        var newHunkLineCount = newHunkEndIndex - hunkStartIndex;
        var hunkStartLine = hunkStartIndex + 1;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"diff --git a/{relativePath} b/{relativePath}");
        builder.AppendLine($"--- a/{relativePath}");
        builder.AppendLine($"+++ b/{relativePath}");
        builder.AppendLine($"@@ -{hunkStartLine},{oldHunkLineCount} +{hunkStartLine},{newHunkLineCount} @@");

        for (var index = hunkStartIndex; index < commonPrefixCount; index++)
        {
            builder.Append(' ').AppendLine(oldLines[index]);
        }

        for (var index = commonPrefixCount; index < oldChangedEndIndex; index++)
        {
            builder.Append('-').AppendLine(oldLines[index]);
        }

        for (var index = commonPrefixCount; index < newChangedEndIndex; index++)
        {
            builder.Append('+').AppendLine(newLines[index]);
        }

        for (var index = oldChangedEndIndex; index < oldHunkEndIndex; index++)
        {
            builder.Append(' ').AppendLine(oldLines[index]);
        }

        return builder.ToString().TrimEnd();
    }

    private static int CountExactOccurrences(string content, string value)
    {
        var count = 0;
        var startIndex = 0;

        while ((startIndex = content.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static LocalCoderPatchValidationResult ValidatePatchPreview(
        string patchText,
        IReadOnlyList<LocalCoderFileContext> fileContexts,
        string task)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview response was empty."]
            };
        }

        var selectedPaths = new HashSet<string>(
            fileContexts.Select(fileContext => NormalizePreviewPath(fileContext.RelativePath)),
            StringComparer.OrdinalIgnoreCase);

        if (selectedPaths.Count == 0)
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview requires selected file context."]
            };
        }

        if (patchText.Contains("Short Diagnosis", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Files Likely Involved", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Step-by-Step Implementation Plan", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Safe Commands to Verify", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Example Implementation", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Here's how", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("Here’s how", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("path/to/file", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("replace with actual values", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("actual values from your project", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("GeneratePatchPreviewButton", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("MyComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("ExampleComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("SampleComponent", StringComparison.OrdinalIgnoreCase) ||
            patchText.Contains("PlaceholderComponent", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview contains forbidden plan or placeholder text."]
            };
        }

        if (string.Equals(patchText, "PATCH_NOT_POSSIBLE", StringComparison.Ordinal))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = true
            };
        }

        if (!patchText.StartsWith("diff --git", StringComparison.Ordinal))
        {
            return new LocalCoderPatchValidationResult
            {
                IsValid = false,
                Errors = ["Patch preview must start with diff --git or PATCH_NOT_POSSIBLE."]
            };
        }

        var errors = new List<string>();
        var lines = patchText
            .ReplaceLineEndings("\n")
            .Split('\n');

        if (!IsValidUnifiedDiff(lines))
        {
            errors.Add("Patch preview contains text outside a valid unified diff.");
        }

        if (!HasMeaningfulAddedLine(lines))
        {
            errors.Add("Patch preview must include at least one meaningful added line.");
        }

        if (!HasRemovedLine(lines))
        {
            errors.Add("Patch preview must include at least one removed line.");
        }

        if (HasOnlyClosingTagChanges(lines))
        {
            errors.Add("Patch preview only changes closing tags.");
        }

        var referencedPaths = ExtractChangedPathsFromDiffHeaders(lines)
            .Select(NormalizePreviewPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (HasUnsafeRazorClosingTagChange(lines))
        {
            errors.Add("Patch preview may break Razor markup by changing closing tag type.");
        }

        if (!PatchAppearsToImplementTask(task, lines))
        {
            errors.Add("Patch preview does not appear to implement the requested change.");
        }

        foreach (var referencedPath in referencedPaths)
        {
            if (string.IsNullOrWhiteSpace(referencedPath) ||
                Path.IsPathRooted(referencedPath) ||
                referencedPath.StartsWith("/", StringComparison.Ordinal) ||
                referencedPath.Contains("..", StringComparison.Ordinal) ||
                !selectedPaths.Contains(referencedPath))
            {
                errors.Add($"Patch preview references a file path not present in the selected file context: {referencedPath}");
            }
        }

        return new LocalCoderPatchValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private static bool PatchAppearsToImplementTask(string task, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return true;
        }

        var addedLines = GetChangedContentLines(lines, '+');
        var removedLines = GetChangedContentLines(lines, '-');
        var quotedValues = ExtractQuotedTaskValues(task);

        if (quotedValues.Count > 0 &&
            !addedLines.Any(line => line.Contains(quotedValues[^1], StringComparison.Ordinal)))
        {
            return false;
        }

        if (!ContainsWholeWord(task, "subtitle"))
        {
            return true;
        }

        var changedLines = removedLines.Concat(addedLines).ToArray();
        if (changedLines.Length > 0 &&
            changedLines.All(line => line.Contains("<RadzenButton", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return HasRadzenTextChangeNearPageHeader(lines);
    }

    private static string[] GetChangedContentLines(IReadOnlyList<string> lines, char prefix)
    {
        var metadataPrefix = new string(prefix, 3);

        return lines
            .Where(line => line.Length > 0 &&
                line[0] == prefix &&
                !line.StartsWith(metadataPrefix, StringComparison.Ordinal))
            .Select(line => line[1..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractQuotedTaskValues(string task)
    {
        return Regex.Matches(task, "[\"']([^\"'\\r\\n]+)[\"']")
            .Select(match => match.Groups[1].Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static bool HasRadzenTextChangeNearPageHeader(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var isChangedLine = (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal)) ||
                                (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal));

            if (!isChangedLine || !line.Contains("<RadzenText", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = Math.Max(0, index - 12);
            var end = Math.Min(lines.Count - 1, index + 12);

            for (var nearbyIndex = start; nearbyIndex <= end; nearbyIndex++)
            {
                var nearbyLine = lines[nearbyIndex];
                if (nearbyLine.Contains("<PageTitle", StringComparison.OrdinalIgnoreCase) ||
                    nearbyLine.Contains("Text=\"Local Coder\"", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string CleanPatchPreviewText(string patchText)
    {
        var cleaned = patchText.ReplaceLineEndings("\n").Trim();

        if (!cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            return cleaned;
        }

        var lines = cleaned
            .Split('\n')
            .ToList();

        if (lines.Count >= 2 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal) && lines[^1].TrimStart().StartsWith("```", StringComparison.Ordinal))
        {
            lines = lines.Skip(1).Take(lines.Count - 2).ToList();
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static bool IsValidUnifiedDiff(IReadOnlyList<string> lines)
    {
        var inHunk = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (!IsValidDiffMetadataLine(line))
                {
                    return false;
                }

                inHunk = false;
                continue;
            }

            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                if (!IsValidIndexMetadataLine(line))
                {
                    return false;
                }

                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("similarity index ", StringComparison.Ordinal)
                || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
                || line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                inHunk = true;
                continue;
            }

            if (inHunk && (line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("-", StringComparison.Ordinal) || line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\\ No newline at end of file", StringComparison.Ordinal)))
            {
                continue;
            }

            return false;
        }

        return lines.Any(line => line.StartsWith("@@ ", StringComparison.Ordinal));
    }

    private static bool IsValidDiffMetadataLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var leftPath = StripDiffPathPrefix(parts[2]);
        var rightPath = StripDiffPathPrefix(parts[3]);

        return IsValidPreviewPathToken(leftPath) &&
               IsValidPreviewPathToken(rightPath);
    }

    private static bool IsValidIndexMetadataLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hashes = parts[1].Split("..", StringSplitOptions.RemoveEmptyEntries);
        if (hashes.Length != 2)
        {
            return false;
        }

        if (!IsAllowedDiffHash(hashes[0]) || !IsAllowedDiffHash(hashes[1]))
        {
            return false;
        }

        return parts.Length == 2 || parts[2].All(char.IsDigit);
    }

    private static bool IsValidPreviewPathToken(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static bool IsAllowedDiffHash(string token)
    {
        var cleaned = token.Trim();
        if (cleaned.Length < 7 || cleaned.Length > 40)
        {
            return false;
        }

        if (!cleaned.All(c => Uri.IsHexDigit(c)))
        {
            return false;
        }

        if (IsPlaceholderDiffHash(cleaned))
        {
            return false;
        }

        return true;
    }

    private static bool IsPlaceholderDiffHash(string token)
    {
        var lower = token.Trim().ToLowerInvariant();
        string[] placeholders =
        [
            "abcdef",
            "abcdef0",
            "1234567",
            "12345678",
            "89abcde",
            "0123456",
            "01234567",
            "deadbeef",
            "cafebabe",
            "feedface"
        ];

        return placeholders.Contains(lower, StringComparer.Ordinal) ||
               lower.Distinct().Count() == 1;
    }

    private static bool HasMeaningfulAddedLine(IReadOnlyList<string> lines)
    {
        return lines.Any(line =>
            line.StartsWith("+", StringComparison.Ordinal) &&
            !line.StartsWith("+++", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(line[1..]));
    }

    private static bool HasRemovedLine(IReadOnlyList<string> lines)
    {
        return lines.Any(line =>
            line.StartsWith("-", StringComparison.Ordinal) &&
            !line.StartsWith("---", StringComparison.Ordinal));
    }

    private static bool HasUnsafeRazorClosingTagChange(IReadOnlyList<string> lines)
    {
        var removedTags = new List<string>();
        var addedTags = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removedTags.Add(line[1..].Trim());
            }
            else if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                addedTags.Add(line[1..].Trim());
            }
        }

        if (removedTags.Count == 0 || addedTags.Count == 0)
        {
            return false;
        }

        if (removedTags.Any(line => line.Contains("</RadzenCard>", StringComparison.OrdinalIgnoreCase)) &&
            addedTags.Any(line => line.Contains("</div>", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var removedClosingTags = removedTags
            .SelectMany(ExtractClosingTags)
            .ToList();

        var addedClosingTags = addedTags
            .SelectMany(ExtractClosingTags)
            .ToList();

        if (removedClosingTags.Count == 0 || addedClosingTags.Count == 0)
        {
            return false;
        }

        if (removedClosingTags.Count != addedClosingTags.Count)
        {
            return true;
        }

        for (var i = 0; i < removedClosingTags.Count; i++)
        {
            if (!removedClosingTags[i].Equals(addedClosingTags[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasOnlyClosingTagChanges(IReadOnlyList<string> lines)
    {
        var changedLines = lines
            .Where(line => (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
                || (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal)))
            .Select(line => line[1..].Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        if (changedLines.Length == 0)
        {
            return false;
        }

        return changedLines.All(line => IsClosingTagOnlyLine(line));
    }

    private static bool IsClosingTagOnlyLine(string line)
    {
        if (!line.Contains("</", StringComparison.Ordinal))
        {
            return false;
        }

        var normalized = line.Replace("<", string.Empty, StringComparison.Ordinal)
            .Replace(">", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Trim();

        return !string.IsNullOrWhiteSpace(normalized) && !normalized.Contains(' ');
    }

    private static IEnumerable<string> ExtractClosingTags(string line)
    {
        var current = line;
        var index = 0;

        while (index >= 0 && index < current.Length)
        {
            index = current.IndexOf("</", index, StringComparison.Ordinal);
            if (index < 0)
            {
                yield break;
            }

            var end = current.IndexOf('>', index);
            if (end < 0)
            {
                yield break;
            }

            yield return current[(index + 2)..end].Trim();
            index = end + 1;
        }
    }

    private static IEnumerable<string> ExtractChangedPathsFromDiffHeaders(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var leftPath = StripDiffPathPrefix(parts[2]);
                    var rightPath = StripDiffPathPrefix(parts[3]);

                    if (!IsDevNullPath(leftPath))
                    {
                        yield return leftPath;
                    }

                    if (!IsDevNullPath(rightPath))
                    {
                        yield return rightPath;
                    }
                }
            }
        }
    }

    private static bool IsPlanningOnlyTask(string task)
    {
        var normalized = task.Trim().ToLowerInvariant();

        string[] strongEditVerbs =
        [
            "add",
            "change",
            "update",
            "replace",
            "remove",
            "rename",
            "fix",
            "refactor",
            "move",
            "create",
            "delete",
            "modify"
        ];

        // A concrete edit verb always takes precedence, including mixed tasks such as
        // "Review the page and change the subtitle."
        if (strongEditVerbs.Any(verb => StartsWithWord(normalized, verb)) ||
            strongEditVerbs.Any(verb => ContainsWholeWord(normalized, verb)))
        {
            return false;
        }

        string[] planningWords =
        [
            "review",
            "suggest",
            "analyze",
            "explain",
            "plan",
            "recommend"
        ];

        string[] planningPhrases =
        [
            "give me a plan",
            "next safe improvement"
        ];

        return planningWords.Any(word => ContainsWholeWord(normalized, word)) ||
               planningPhrases.Any(normalized.Contains);
    }

    private static bool StartsWithWord(string text, string word)
    {
        return text.StartsWith(word, StringComparison.Ordinal) &&
               (text.Length == word.Length || !char.IsLetterOrDigit(text[word.Length]));
    }

    private static bool ContainsWholeWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.Ordinal);

        while (index >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterOk = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);

            if (beforeOk && afterOk)
            {
                return true;
            }

            index = text.IndexOf(word, index + word.Length, StringComparison.Ordinal);
        }

        return false;
    }

    private static string StripDiffPathPrefix(string path)
    {
        var trimmed = path.Trim();

        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            return trimmed[2..];
        }

        return trimmed;
    }

    private static bool IsDevNullPath(string path)
    {
        return string.Equals(path.Replace('\\', '/').Trim(), "/dev/null", StringComparison.Ordinal);
    }

    private static string NormalizePreviewPath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }

    private static string TrimForPrompt(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content ?? string.Empty;
        }

        return content[..maxChars];
    }

    private static string EscapeBash(string command)
    {
        return command.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaModel> Models { get; set; } = [];
    }

    private sealed class OllamaModel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
    }

    public async Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath)
    {
        var results = new List<CommandRunResult>();

        string[] commands =
        [
            "git status",
            "dotnet build",
            "dotnet test"
        ];

        foreach (var command in commands)
        {
            var result = await RunCommandAsync(projectPath, command);
            results.Add(result);
        }

        return results;
    }
}
