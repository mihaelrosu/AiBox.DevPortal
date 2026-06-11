namespace AiBox.DevPortal.Models;

public enum PatchValidationCategory
{
    RazorMarkupRisk,
    HtmlStructureRisk,
    MissingAnchor,
    AmbiguousAnchor,
    UnsafeFileOperation
}

public sealed record PatchValidationGuidance(
    PatchValidationCategory Category,
    string Reason,
    string SuggestedFix);

public static class PatchValidationGuidanceFactory
{
    public static IReadOnlyList<PatchValidationGuidance> Create(IReadOnlyList<string> validationErrors)
    {
        return validationErrors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Select(Create)
            .ToArray();
    }

    private static PatchValidationGuidance Create(string error)
    {
        if (ContainsAny(error, "ambiguous", "multiple occurrences", "not unique"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.AmbiguousAnchor,
                error,
                "Use a longer, unique anchor or replace the exact surrounding block so only one location can match.");
        }

        if (ContainsAny(error, "anchor not found", "old text not found"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.MissingAnchor,
                error,
                "Use text copied exactly from the current selected file context, preferably a complete element or component boundary.");
        }

        if (ContainsAny(error, "razor markup", "razor component", "razor structure"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.RazorMarkupRisk,
                error,
                "Preserve complete Razor component boundaries. Prefer replacing the exact component block or inserting after a complete closing component tag.");
        }

        if (ContainsAny(error, "closing tag", "html structure", "html markup"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.HtmlStructureRisk,
                error,
                "Target a complete HTML boundary: use insert_after \"</p>\", insert_after the exact heading/component, or replace the exact paragraph block.");
        }

        return new PatchValidationGuidance(
            PatchValidationCategory.UnsafeFileOperation,
            error,
            "Use a supported operation against a file in the selected context, with a safe project-relative path and an exact target.");
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
