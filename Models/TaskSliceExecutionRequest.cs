namespace AiBox.DevPortal.Models;

public sealed class TaskSliceExecutionRequest
{
    public TaskPlan Plan { get; set; } = new();
    public TaskSlice Slice { get; set; } = new();
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public string RequestedBy { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
