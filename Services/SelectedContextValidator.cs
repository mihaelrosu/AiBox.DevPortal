using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class SelectedContextValidator
{
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".yml", ".yaml", ".css", ".html", ".js", ".md", ".csproj", ".sln", ".props", ".targets"
    };

    public void ValidateForPatchPreview(IEnumerable<LocalCoderFileContext> fileContexts)
    {
        var contexts = (fileContexts ?? [])
            .Where(file => file is not null)
            .ToArray();

        var editableFiles = contexts
            .Where(IsEditableSourceFile)
            .ToArray();

        if (editableFiles.Length == 0)
        {
            throw CreateValidationException("No editable source files selected.");
        }

        if (editableFiles.Any(file => file.IsTruncated))
        {
            throw CreateValidationException("Selected file context is incomplete. Patch preview requires full file contents.");
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
