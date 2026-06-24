namespace AiBox.DevPortal.Models;

public sealed class TaskWorkflowSlice
{
    public string SliceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public IReadOnlyList<string> TargetFiles { get; set; } = [];
    public string RiskLevel { get; set; } = string.Empty;
    public IReadOnlyList<string> DependsOnSliceIds { get; set; } = [];
}
