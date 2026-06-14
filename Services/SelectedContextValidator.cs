using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class SelectedContextValidator
{
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".yml", ".yaml", ".css", ".html", ".js", ".md", ".csproj", ".sln", ".props", ".targets"
    };

    public void ValidateForPatchPreview(IEnumerable<LocalCoderFileContext> fileContexts, PatchIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var contexts = (fileContexts ?? [])
            .Where(file => file is not null)
            .ToArray();

        var editableFiles = contexts
            .Where(IsEditableSourceFile)
            .ToArray();

        if (editableFiles.Any(file => file.IsTruncated))
        {
            throw CreateValidationException("Selected file context is incomplete. Patch preview requires full file contents.");
        }

        if (editableFiles.Length > 0)
        {
            return;
        }

        if (HasValidCreateTargets(intent))
        {
            return;
        }

        if (editableFiles.Length == 0)
        {
            throw CreateValidationException("No editable source files selected and no valid create targets were detected.");
        }
    }

    private static bool IsEditableSourceFile(LocalCoderFileContext file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.RelativePath))
        {
            return false;
        }

        if (file.IsGeneratedFile)
        {
            return false;
        }

        if (IsAgentInstructionsFile(file.RelativePath))
        {
            return false;
        }

        return EditableExtensions.Contains(Path.GetExtension(file.RelativePath));
    }

    private static bool IsAgentInstructionsFile(string relativePath)
    {
        return Path.GetFileName(relativePath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasValidCreateTargets(PatchIntent intent)
    {
        var createTargets = (intent.TargetCreatedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (createTargets.Length == 0)
        {
            return false;
        }

        var allowedCreateFolders = (intent.AllowedCreateFolders ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (allowedCreateFolders.Length == 0)
        {
            return false;
        }

        return createTargets.All(target => IsUnderAnyFolder(target, allowedCreateFolders));
    }

    private static bool IsUnderAnyFolder(string path, IReadOnlyList<string> allowedFolders)
    {
        return allowedFolders.Any(folder => IsUnderFolder(path, folder));
    }

    private static bool IsUnderFolder(string path, string folder)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedFolder = NormalizeFolder(folder);

        return normalizedPath.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedFolder, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFolder(string path)
    {
        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.EndsWith("/", StringComparison.Ordinal)
            ? normalized
            : $"{normalized}/";
    }

    private static string NormalizePath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim();
    }

    private static PatchPreviewValidationException CreateValidationException(string message)
    {
        return new PatchPreviewValidationException(
            message,
            [message],
            string.Empty,
            string.Empty,
            string.Empty);
    }
}
