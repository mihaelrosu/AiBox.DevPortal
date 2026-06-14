namespace AiBox.DevPortal.Models;

public sealed class PlannerResult
{
    public IReadOnlyList<string> TargetFiles { get; set; } = [];

    public IReadOnlyList<string> InstructionFiles { get; set; } = [];

    public IReadOnlyList<string> Rules { get; set; } = [];
}
