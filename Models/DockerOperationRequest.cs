namespace AiBox.DevPortal.Models;

public sealed class DockerOperationRequest
{
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public DockerOperationType OperationType { get; set; } = DockerOperationType.Ps;
    public string ComposeFilePath { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int Lines { get; set; } = 100;
    public bool Confirmed { get; set; }
}
