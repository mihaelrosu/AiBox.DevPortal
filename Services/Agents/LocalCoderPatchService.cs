using System.Text.RegularExpressions;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Models.Repositories;
using AiBox.DevPortal.Services.Repositories;

namespace AiBox.DevPortal.Services.Agents;

public sealed partial class LocalCoderPatchService(
    IOllamaService ollamaService,
    IRepositoryFileContextService repositoryFileContextService) : ILocalCoderPatchService
{
    private const int MaxPatchCharacters = 256 * 1024;
    private static readonly HashSet<string> BinaryAndMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".class", ".dll", ".doc", ".docx", ".exe", ".flac",
        ".gif", ".gz", ".ico", ".jar", ".jpeg", ".jpg", ".mkv", ".mov", ".mp3", ".mp4",
        ".pdf", ".png", ".ppt", ".pptx", ".rar", ".so", ".svg", ".tar", ".tiff", ".wav",
        ".webm", ".webp", ".xls", ".xlsx", ".zip"
    };

    public async Task<LocalCoderResult> GeneratePatchAsync(
        LocalCoderTask task,
        string planText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (task.SelectedFilePaths is null || task.SelectedFilePaths.Count == 0)
        {
            return Failure("Select one or more safe repository files before generating a patch.");
        }

        try
        {
            var context = await repositoryFileContextService.ReadAsync(
                task.RepositoryPath,
                task.SelectedFilePaths,
                cancellationToken);
            var response = await ollamaService.GenerateAsync(
                task.Model,
                BuildPatchPrompt(task, planText, context),
                cancellationToken);
            var patch = ExtractPatch(response);
            var validation = ValidatePatch(task, patch);

            if (!validation.IsValid)
            {
                return Failure($"Generated patch was rejected:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", validation.Errors)}");
            }

            return Success(patch);
        }
        catch (Exception exception)
        {
            return Failure($"Patch generation failed safely. No files were changed. {exception.Message}");
        }
    }

    public LocalCoderPatchValidationResult ValidatePatch(LocalCoderTask task, string patchText)
    {
        ArgumentNullException.ThrowIfNull(task);
        var errors = new List<string>();
        var touchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(patchText))
        {
            errors.Add("Patch text is empty.");
            return Invalid(errors, touchedPaths);
        }

        if (patchText.Length > MaxPatchCharacters)
        {
            errors.Add($"Patch exceeds the {MaxPatchCharacters / 1024} KB limit.");
        }

        if (patchText.Contains("GIT binary patch", StringComparison.OrdinalIgnoreCase)
            || patchText.Contains("Binary files ", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Binary patches are not allowed.");
        }

        if (patchText.Contains("rename from ", StringComparison.OrdinalIgnoreCase)
            || patchText.Contains("rename to ", StringComparison.OrdinalIgnoreCase)
            || patchText.Contains("copy from ", StringComparison.OrdinalIgnoreCase)
            || patchText.Contains("copy to ", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Rename and copy patches are not allowed.");
        }

        var diffHeaders = DiffHeaderRegex().Matches(patchText);
        var oldFileHeaders = OldFileHeaderRegex().Matches(patchText);
        var newFileHeaders = NewFileHeaderRegex().Matches(patchText);
        var hunks = HunkHeaderRegex().Matches(patchText);
        var newFilePaths = diffHeaders
            .Cast<Match>()
            .Where(match => IsDevNullPath(match.Groups["old"].Value))
            .Select(match => NormalizeDiffPath(match.Groups["new"].Value))
            .Where(path => !string.IsNullOrWhiteSpace(path) && !IsDevNullPath(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (diffHeaders.Count == 0)
        {
            errors.Add("Patch must contain a line starting with 'diff --git '.");
        }

        if (oldFileHeaders.Count == 0)
        {
            errors.Add("Patch must contain a line starting with '--- '.");
        }

        if (newFileHeaders.Count == 0)
        {
            errors.Add("Patch must contain a line starting with '+++ '.");
        }

        if (hunks.Count == 0)
        {
            errors.Add("Patch must contain a valid unified diff hunk line starting with '@@ '.");
        }

        foreach (Match match in diffHeaders)
        {
            var allowUnselected = IsDevNullPath(match.Groups["old"].Value);
            ValidateTouchedPath(task, match.Groups["old"].Value, touchedPaths, errors, allowUnselected);
            ValidateTouchedPath(task, match.Groups["new"].Value, touchedPaths, errors, allowUnselected);
        }

        foreach (Match match in oldFileHeaders.Cast<Match>().Concat(newFileHeaders.Cast<Match>()))
        {
            var allowUnselected = newFilePaths.Contains(NormalizeDiffPath(match.Groups["path"].Value));
            ValidateTouchedPath(task, match.Groups["path"].Value, touchedPaths, errors, allowUnselected);
        }

        if (touchedPaths.Count == 0)
        {
            errors.Add("Patch does not contain any valid repository-relative changed file paths.");
        }

        return new LocalCoderPatchValidationResult
        {
            IsValid = errors.Count == 0,
            TouchedPaths = touchedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static void ValidateTouchedPath(
        LocalCoderTask task,
        string rawPath,
        HashSet<string> touchedPaths,
        List<string> errors,
        bool allowUnselected)
    {
        var path = NormalizeDiffPath(rawPath);

        if (string.IsNullOrWhiteSpace(path) || path == "/dev/null")
        {
            return;
        }

        if (IsAbsolutePath(path) || path.Split('/').Any(segment => segment == ".."))
        {
            errors.Add($"Patch path '{path}' must be repository-relative and cannot contain '..'.");
            return;
        }

        if (!IsInsideRepository(task.RepositoryPath, path))
        {
            errors.Add($"Patch path '{path}' resolves outside repository '{task.RepositoryPath}'.");
            return;
        }

        if (IsSecretPath(path))
        {
            errors.Add($"Patch path '{path}' is blocked because it may contain secrets or production settings.");
            return;
        }

        if (IsBinaryOrMediaPath(path))
        {
            errors.Add($"Patch path '{path}' is blocked because binary and media files are not allowed.");
            return;
        }

        var selected = task.SelectedFilePaths ?? [];
        if (!allowUnselected && !selected.Any(item => NormalizePath(item).Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Patch path '{path}' was not explicitly selected for context.");
            return;
        }

        if (IsForbidden(task.ForbiddenPathsText, path))
        {
            errors.Add($"Patch path '{path}' is forbidden by the task.");
            return;
        }

        if (!IsAllowed(task.AllowedPathsText, path))
        {
            errors.Add($"Patch path '{path}' is outside the task's allowed paths.");
            return;
        }

        touchedPaths.Add(path);
    }

    private static string BuildPatchPrompt(
        LocalCoderTask task,
        string planText,
        IReadOnlyList<RepositoryFileContent> context)
    {
        return $"""
            You are a coding assistant generating a unified diff.

            Return only a valid unified diff.
            Do not use Markdown fences.
            Do not explain.
            Do not claim files were changed.
            Only modify allowed paths and explicitly selected files included below.
            Never modify forbidden paths.
            Never modify secrets or production settings.
            Use repository-relative paths.
            Do not create, delete, rename, or copy files.
            Prefer minimal changes.

            Task:
            {task.Instructions}

            Repository:
            {task.RepositoryPath}

            Allowed paths:
            {task.AllowedPathsText}

            Forbidden paths:
            {task.ForbiddenPathsText}

            Selected context files:
            {FormatContext(context)}

            Plan:
            {(string.IsNullOrWhiteSpace(planText) ? "(No plan available.)" : planText)}

            Generate the smallest safe patch.
            """;
    }

    private static string FormatContext(IReadOnlyList<RepositoryFileContent> files)
    {
        return string.Join(
            Environment.NewLine,
            files.Select(file => $"""
                --- {file.RelativePath} ---
                {file.Content}
                --- end {file.RelativePath} ---
                """));
    }

    private static string ExtractPatch(string response)
    {
        var text = response.Trim();
        var diffStart = text.IndexOf("diff --git ", StringComparison.Ordinal);

        if (diffStart >= 0)
        {
            text = text[diffStart..];
        }

        if (text.EndsWith("```", StringComparison.Ordinal))
        {
            text = text[..^3].TrimEnd();
        }

        return text;
    }

    private static bool IsAllowed(string allowedPathsText, string path)
    {
        var allowed = ParsePaths(allowedPathsText);
        return allowed.Count == 0 || allowed.Any(item => IsPathOrChild(path, item));
    }

    private static bool IsForbidden(string forbiddenPathsText, string path)
    {
        return ParsePaths(forbiddenPathsText).Any(item => IsPathOrChild(path, item));
    }

    private static IReadOnlyList<string> ParsePaths(string text)
    {
        return (text ?? string.Empty)
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPathOrChild(string path, string parent)
    {
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDiffPath(string path)
    {
        var normalized = path.Trim().Trim('"').Replace('\\', '/');
        return normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static bool IsDevNullPath(string path)
    {
        return string.Equals(NormalizeDiffPath(path), "/dev/null", StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimStart('.', '/', '\\').Replace('\\', '/').TrimEnd('/');
    }

    private static bool IsSecretPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = segments.LastOrDefault() ?? string.Empty;
        return segments.Any(segment => segment is ".git" or ".ssh" or ".aws" or ".azure" or ".gnupg" or ".kube" or "secrets")
            || fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings.production", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBinaryOrMediaPath(string path)
    {
        return BinaryAndMediaExtensions.Contains(Path.GetExtension(path));
    }

    private static bool IsAbsolutePath(string path)
    {
        return Path.IsPathRooted(path)
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] == '/');
    }

    private static bool IsInsideRepository(string repositoryPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(repositoryPath.Trim()).TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootPrefix, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static LocalCoderResult Success(string output) => new()
    {
        Success = true,
        Output = output,
        CreatedAt = DateTime.UtcNow
    };

    private static LocalCoderResult Failure(string error) => new()
    {
        Success = false,
        ErrorMessage = error,
        CreatedAt = DateTime.UtcNow
    };

    private static LocalCoderPatchValidationResult Invalid(
        IReadOnlyList<string> errors,
        IReadOnlySet<string> touchedPaths) => new()
    {
        IsValid = false,
        TouchedPaths = touchedPaths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        Errors = errors.ToArray()
    };

    [GeneratedRegex(@"^diff --git\s+(?<old>(?:""[^""]+""|\S+))\s+(?<new>(?:""[^""]+""|\S+))\s*$", RegexOptions.Multiline)]
    private static partial Regex DiffHeaderRegex();

    [GeneratedRegex(@"^---\s+(?<path>(?:""[^""]+""|\S+))(?:\s.*)?$", RegexOptions.Multiline)]
    private static partial Regex OldFileHeaderRegex();

    [GeneratedRegex(@"^\+\+\+\s+(?<path>(?:""[^""]+""|\S+))(?:\s.*)?$", RegexOptions.Multiline)]
    private static partial Regex NewFileHeaderRegex();

    [GeneratedRegex(@"^@@\s+-\d+(?:,\d+)?\s+\+\d+(?:,\d+)?\s+@@(?:\s.*)?$", RegexOptions.Multiline)]
    private static partial Regex HunkHeaderRegex();
}
