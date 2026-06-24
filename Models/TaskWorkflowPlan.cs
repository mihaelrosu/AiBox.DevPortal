namespace AiBox.DevPortal.Models;

public sealed class TaskWorkflowPlan
{
    public string Goal { get; set; } = string.Empty;

    public IReadOnlyList<TaskWorkflowSlice> Slices { get; set; } = [];

    public string RecommendedNextSliceId { get; set; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; set; } = [];
}