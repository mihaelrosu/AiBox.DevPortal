namespace AiBox.DevPortal.Models;

public sealed class WorkflowRunRecord
{
    public string Id { get; set; } = string.Empty;
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string GoalText { get; set; } = string.Empty;
    public string? ProjectName { get; set; }
    public DateTimeOffset Created { get; set; }
    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Planned;
    public List<WorkflowRunStepRecord> Steps { get; set; } = [];
}
