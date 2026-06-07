namespace AiBox.DevPortal.Models;

public sealed class ComfyUiOperationResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public ComfyUiOperationType OperationType { get; set; } = ComfyUiOperationType.Health;
    public string WorkflowFileName { get; set; } = string.Empty;
    public string WorkflowJson { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public string PromptId { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
