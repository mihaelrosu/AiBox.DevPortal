namespace AiBox.DevPortal.Models;

public sealed class WorkflowRunPreviewRequest
{
    public string WorkflowId { get; set; } = string.Empty;
    public string GoalText { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
}
