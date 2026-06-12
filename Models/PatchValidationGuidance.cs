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
    string SuggestedFix,
    string OriginalOperation,
    string SaferOperation);

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
                "Use a longer, unique anchor or replace the exact surrounding block so only one location can match.",
                "insert_after a repeated fragment",
                "replace the exact surrounding block");
        }

        if (ContainsAny(error, "anchor not found", "old text not found"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.MissingAnchor,
                error,
                "Use text copied exactly from the current selected file context, preferably a complete element or component boundary.",
                "insert_after a partial fragment",
                "replace the complete surrounding block");
        }

        if (ContainsAny(error, "razor markup", "razor component", "razor structure"))
        {
            var saferOperation = ContainsAny(error, "card")
                ? "replace complete card section"
                : "replace complete header block";

            var originalOperation = ContainsAny(error, "card")
                ? "insert_after <RadzenCard>"
                : "insert_after <h1>";

            return new PatchValidationGuidance(
                PatchValidationCategory.RazorMarkupRisk,
                error,
                "Preserve complete Razor component boundaries. Prefer replacing the exact component block or inserting after a complete closing component tag.",
                originalOperation,
                saferOperation);
        }

        if (ContainsAny(error, "closing tag", "html structure", "html markup"))
        {
            return new PatchValidationGuidance(
                PatchValidationCategory.HtmlStructureRisk,
                error,
                "Target a complete HTML boundary: use insert_after \"</p>\", insert_after the exact heading/component, or replace the exact paragraph block.",
                "insert_after a partial HTML boundary",
                "replace the complete HTML block");
        }

        return new PatchValidationGuidance(
            PatchValidationCategory.UnsafeFileOperation,
            error,
            "Use a supported operation against a file in the selected context, with a safe project-relative path and an exact target.",
            "unsafe or unsupported operation",
            "use a supported operation against an exact target");
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }
}
