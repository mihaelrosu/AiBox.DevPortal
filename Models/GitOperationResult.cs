namespace AiBox.DevPortal.Models;

public sealed class GitOperationResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string StepId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public GitOperationType OperationType { get; set; } = GitOperationType.Status;
    public string BranchName { get; set; } = string.Empty;
    public string FilePaths { get; set; } = string.Empty;
    public string CommitMessage { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public int? ExitCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
