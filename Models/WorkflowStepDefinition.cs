namespace AiBox.DevPortal.Models;

public sealed class WorkflowStepDefinition
{
    public string Id { get; set; } = string.Empty;
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkflowStepType Type { get; set; } = WorkflowStepType.Agent;
    public string AgentId { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool IncludePreviousResults { get; set; }
    public PreviousResultMode PreviousResultMode { get; set; } = PreviousResultMode.None;
    public List<string> DependsOnStepIds { get; set; } = [];
}
