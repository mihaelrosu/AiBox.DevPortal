namespace AiBox.DevPortal.Models;

public sealed class WorkflowRunStepPreview
{
    public string StepId { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
    public string StepName { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Instruction { get; set; } = string.Empty;
    public string GeneratedTaskPrompt { get; set; } = string.Empty;
    public bool IncludePreviousResults { get; set; }
    public PreviousResultMode PreviousResultMode { get; set; } = PreviousResultMode.None;
    public List<string> DependsOnStepIds { get; set; } = [];
}
