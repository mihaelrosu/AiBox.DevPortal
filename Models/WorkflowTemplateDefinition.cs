namespace AiBox.DevPortal.Models;

public sealed class WorkflowTemplateDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorkflowTemplateCategory Category { get; set; }
    public bool Enabled { get; set; } = true;
    public List<WorkflowStepDefinition> Steps { get; set; } = [];
}
