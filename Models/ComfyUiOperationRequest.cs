namespace AiBox.DevPortal.Models;

public sealed class ComfyUiOperationRequest
{
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public ComfyUiOperationType OperationType { get; set; } = ComfyUiOperationType.Health;
    public string WorkflowFileName { get; set; } = string.Empty;
    public string WorkflowJson { get; set; } = string.Empty;
    public bool Confirmed { get; set; }
}
