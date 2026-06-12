namespace AiBox.DevPortal.Models;

public enum RazorStructureRegion
{
    DirectiveRegion,
    ImportRegion,
    DocumentationRegion,
    MarkupRegion,
    CodeRegion,
    UnknownRegion
}

public sealed class RazorStructureLineClassification
{
    public string Text { get; set; } = string.Empty;
    public RazorStructureRegion Region { get; set; } = RazorStructureRegion.UnknownRegion;
}

public sealed class RazorStructureHunkClassification
{
    public string FilePath { get; set; } = string.Empty;
    public string HunkHeader { get; set; } = string.Empty;
    public RazorStructureRegion Region { get; set; } = RazorStructureRegion.UnknownRegion;
    public IReadOnlyList<RazorStructureLineClassification> ChangedLines { get; set; } = [];
}

public sealed class RazorStructureGuardResult
{
    public IReadOnlyList<RazorStructureHunkClassification> Hunks { get; set; } = [];
    public IReadOnlyList<string> Errors { get; set; } = [];
    public bool IsValid => Errors.Count == 0;
}
