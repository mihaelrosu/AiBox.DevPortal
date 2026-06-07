namespace AiBox.DevPortal.Models;

public sealed class FileOperationResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public FileOperationType OperationType { get; set; } = FileOperationType.Read;
    public string Path { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ContentPreview { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
