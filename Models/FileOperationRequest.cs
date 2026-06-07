namespace AiBox.DevPortal.Models;

public sealed class FileOperationRequest
{
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public FileOperationType OperationType { get; set; } = FileOperationType.Read;
    public string Path { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool Confirmed { get; set; }
}
