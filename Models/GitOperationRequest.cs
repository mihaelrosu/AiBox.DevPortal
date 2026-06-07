namespace AiBox.DevPortal.Models;

public sealed class GitOperationRequest
{
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public GitOperationType OperationType { get; set; } = GitOperationType.Status;
    public string BranchName { get; set; } = string.Empty;
    public string FilePaths { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public bool Confirmed { get; set; }
}
