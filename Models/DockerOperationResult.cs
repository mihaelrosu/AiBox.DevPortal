namespace AiBox.DevPortal.Models;

public sealed class DockerOperationResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public DockerOperationType OperationType { get; set; } = DockerOperationType.Ps;
    public string ComposeFilePath { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int Lines { get; set; }
    public string WorkingDirectory { get; set; } = string.Empty;
    public string CommandText { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
