using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

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

    public async Task<LocalCoderTask> CreatePlanAsync(LocalCoderRequest request)
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

        var task = new LocalCoderTask
        {
            ProjectPath = request.ProjectPath,
            Model = ollamaRequest.Model,
            Task = request.Task,
            Plan = result?.Response?.Trim() ?? string.Empty
        };

        await SaveTaskAsync(task);
        return task;
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

    public async Task<IReadOnlyList<LocalCoderTask>> GetHistoryAsync()
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
            var tasks = await JsonSerializer.DeserializeAsync<List<LocalCoderTask>>(stream, JsonOptions);
            return tasks?.OrderByDescending(x => x.Created).ToArray() ?? [];
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task SaveTaskAsync(LocalCoderTask task)
    {
        await FileLock.WaitAsync();

        try
        {
            var path = GetHistoryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var tasks = new List<LocalCoderTask>();

            if (File.Exists(path))
            {
                await using var readStream = File.OpenRead(path);
                tasks = await JsonSerializer.DeserializeAsync<List<LocalCoderTask>>(readStream, JsonOptions) ?? [];
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
