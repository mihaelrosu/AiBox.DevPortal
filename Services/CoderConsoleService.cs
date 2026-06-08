using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;
using AgentLocalCoderTask = AiBox.DevPortal.Models.Agents.LocalCoderTask;

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

    public async Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request)
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

        var prompt = $$"""
        You are a local coding assistant for a C# Blazor/Radzen project.

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

    public async Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request)
    {
        ValidateProjectPath(request.ProjectPath);

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new InvalidOperationException("Task is required.");
        }

        request.FileContexts = (request.FileContexts ?? [])
            .Take(MaxSelectedFileCount)
            .ToList();

        var fileContextText = BuildPatchPreviewFileContextText(request.FileContexts);
        var selectedFilePathsText = BuildSelectedFilePathsText(request.FileContexts);

        var prompt = $$"""
        You are a coding assistant generating a patch preview for a C# Blazor/Radzen project.

        Return only one of these formats.

        Preferred format:
        ```diff
        diff --git a/path/to/file b/path/to/file
        --- a/path/to/file
        +++ b/path/to/file
        @@ ...
        ```

        Fallback format:
        ```text
        FILE: path/to/file
        REPLACE:
        ...
        WITH:
        ...
        ```

        Do not explain unless the patch cannot be produced.
        Do not include markdown fences.
        Do not include destructive commands.
        Only modify files from the selected file context.
        Never use placeholder paths, fake component names, or generic instructions.
        Never use template text like path/to/file, replace with actual values, or actual values from your project if available.
        If a file is missing from context, say which file is needed instead of inventing its full contents.
        If you cannot create a concrete patch, return exactly:
        PATCH_NOT_POSSIBLE
        Reason: <why a concrete patch cannot be produced>
        Needed files: <comma-separated exact file paths>

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
        var patchText = result?.Response?.Trim() ?? string.Empty;
        var validation = ValidatePatchPreview(patchText, request.FileContexts);

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

    private static LocalCoderPatchValidationResult ValidatePatchPreview(string patchText, IReadOnlyList<LocalCoderFileContext> fileContexts)
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

        if (patchText.Contains("PATCH_NOT_POSSIBLE", StringComparison.OrdinalIgnoreCase))
        {
            if (!Regex.IsMatch(patchText, @"Reason:\s*\S+", RegexOptions.IgnoreCase)
                || !Regex.IsMatch(patchText, @"Needed files:\s*\S+", RegexOptions.IgnoreCase))
            {
                return new LocalCoderPatchValidationResult
                {
                    IsValid = false,
                    Errors = ["PATCH_NOT_POSSIBLE responses must include both a reason and needed files."]
                };
            }

            return new LocalCoderPatchValidationResult
            {
                IsValid = true
            };
        }

        var errors = new List<string>();
        var forbiddenMarkers = new[]
        {
            "path/to/file",
            "<GeneratePatchPreviewButton",
            "replace with actual values",
            "actual values from your project"
        };

        foreach (var marker in forbiddenMarkers)
        {
            if (patchText.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Patch preview contains placeholder text: {marker}");
            }
        }

        var lines = patchText
            .ReplaceLineEndings("\n")
            .Split('\n');

        if (!IsConcretePatchFormat(lines))
        {
            errors.Add("Patch preview contains explanations or unsupported text outside the patch format.");
        }

        var referencedPaths = ExtractReferencedPaths(lines)
            .Select(NormalizePreviewPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var referencedPath in referencedPaths)
        {
            if (!selectedPaths.Contains(referencedPath))
            {
                errors.Add($"Patch preview references a file path not present in the selected file context: {referencedPath}");
            }
        }

        if (ContainsNoOpContent(lines, fileContexts))
        {
            errors.Add("Patch preview duplicates existing selected file content and does not describe a concrete change.");
        }

        if (Regex.IsMatch(patchText, @"\breplace with actual values\b", RegexOptions.IgnoreCase))
        {
            errors.Add("Patch preview contains generic replacement instructions.");
        }

        return new LocalCoderPatchValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private static bool IsConcretePatchFormat(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return false;
        }

        var firstMeaningfulLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (firstMeaningfulLine is null)
        {
            return false;
        }

        if (firstMeaningfulLine.StartsWith("PATCH_NOT_POSSIBLE", StringComparison.OrdinalIgnoreCase))
        {
            return Regex.IsMatch(string.Join("\n", lines), @"Reason:\s*\S+", RegexOptions.IgnoreCase)
                && Regex.IsMatch(string.Join("\n", lines), @"Needed files:\s*\S+", RegexOptions.IgnoreCase);
        }

        if (firstMeaningfulLine.StartsWith("diff --git", StringComparison.Ordinal))
        {
            return IsValidUnifiedDiff(lines);
        }

        if (firstMeaningfulLine.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
        {
            return IsValidFallbackPatch(lines);
        }

        return false;
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
                inHunk = false;
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
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

            if (inHunk && (line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("-", StringComparison.Ordinal) || line.StartsWith(" ", StringComparison.Ordinal)))
            {
                continue;
            }

            return false;
        }

        return lines.Any(line => line.StartsWith("@@ ", StringComparison.Ordinal)) || lines.Any(line => line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidFallbackPatch(IReadOnlyList<string> lines)
    {
        var hasFile = false;
        var hasReplace = false;
        var hasWith = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                hasFile = true;
                hasReplace = false;
                hasWith = false;
                continue;
            }

            if (line.StartsWith("REPLACE:", StringComparison.OrdinalIgnoreCase))
            {
                hasReplace = true;
                continue;
            }

            if (line.StartsWith("WITH:", StringComparison.OrdinalIgnoreCase))
            {
                hasWith = true;
                continue;
            }

            if (!hasFile || (!hasReplace && !hasWith))
            {
                return false;
            }
        }

        return hasFile && hasReplace && hasWith;
    }

    private static IEnumerable<string> ExtractReferencedPaths(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    yield return StripDiffPathPrefix(parts[2]);
                    yield return StripDiffPathPrefix(parts[3]);
                }
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return StripDiffPathPrefix(path);
                }
            }
            else if (line.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                var path = line[5..].Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
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

    private static string NormalizePreviewPath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }

    private static bool ContainsNoOpContent(IReadOnlyList<string> lines, IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        var contextText = string.Join(Environment.NewLine, fileContexts.Select(fileContext => fileContext.Content));

        if (string.IsNullOrWhiteSpace(contextText))
        {
            return false;
        }

        var normalizedContextText = contextText.ReplaceLineEndings("\n");
        var addedLines = new List<string>();
        var deletedLines = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                var content = line[1..].Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    addedLines.Add(content);
                }
            }
            else if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal))
            {
                var content = line[1..].Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    deletedLines.Add(content);
                }
            }
            else if (line.StartsWith("REPLACE:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("WITH:", StringComparison.OrdinalIgnoreCase))
            {
                // handled by fallback parser below
            }
        }

        if (addedLines.Count > 0 && addedLines.All(line => normalizedContextText.Contains(line, StringComparison.Ordinal)))
        {
            return true;
        }

        if (deletedLines.Count > 0 && deletedLines.All(line => normalizedContextText.Contains(line, StringComparison.Ordinal)))
        {
            return true;
        }

        var fallbackText = string.Join("\n", lines);
        if (fallbackText.Contains("FILE:", StringComparison.OrdinalIgnoreCase)
            && fallbackText.Contains("REPLACE:", StringComparison.OrdinalIgnoreCase)
            && fallbackText.Contains("WITH:", StringComparison.OrdinalIgnoreCase))
        {
            var replaceIndex = fallbackText.IndexOf("REPLACE:", StringComparison.OrdinalIgnoreCase);
            var withIndex = fallbackText.IndexOf("WITH:", StringComparison.OrdinalIgnoreCase);
            if (replaceIndex >= 0 && withIndex > replaceIndex)
            {
                var replaceText = fallbackText[(replaceIndex + "REPLACE:".Length)..withIndex].Trim();
                var afterWith = fallbackText[(withIndex + "WITH:".Length)..].Trim();

                if (!string.IsNullOrWhiteSpace(replaceText)
                    && !string.IsNullOrWhiteSpace(afterWith)
                    && replaceText.Equals(afterWith, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
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

        if (result.ExitCode != 0)
        {
            break;
        }
    }

    return results;
}
}
