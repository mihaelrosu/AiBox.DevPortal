namespace AiBox.DevPortal.Models;

public sealed class PlannerContextSelectionResult
{
    public IReadOnlyList<string> EditableFiles { get; set; } = [];

    public IReadOnlyList<string> InstructionFiles { get; set; } = [];

    public IReadOnlyList<string> MissingFiles { get; set; } = [];

    public IReadOnlyList<string> Warnings { get; set; } = [];

    public IReadOnlyList<string> Rules { get; set; } = [];

    public bool IsPatchBuilderBlocked => EditableFiles.Count == 0;
}
