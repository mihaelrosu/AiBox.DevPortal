namespace AiBox.DevPortal.Models;

public sealed class WorkflowRunPreviewResult
{
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string GoalText { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public ProjectSnapshot? ProjectSnapshot { get; set; }
    public List<WorkflowRunStepPreview> Steps { get; set; } = [];
}
