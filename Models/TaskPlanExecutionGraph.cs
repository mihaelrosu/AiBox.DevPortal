namespace AiBox.DevPortal.Models;

public sealed class TaskPlanExecutionGraph
{
    public List<string> OrderedSliceIds { get; set; } = new();
    public List<string> CyclesDetected { get; set; } = new();
    public List<string> ValidationErrors { get; set; } = new();

    public bool IsValid => CyclesDetected.Count == 0 && ValidationErrors.Count == 0;
}
