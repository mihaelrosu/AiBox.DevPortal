namespace AiBox.DevPortal.Models;

public sealed class TaskSliceExecutionRequest
{
    public TaskPlan Plan { get; set; } = new();
    public TaskSlice Slice { get; set; } = new();
}