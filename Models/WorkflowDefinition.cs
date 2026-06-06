namespace AiBox.DevPortal.Models;

public sealed class WorkflowDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Active;
    public List<WorkflowStepDefinition> Steps { get; set; } = [];
}
