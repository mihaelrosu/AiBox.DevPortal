namespace AiBox.DevPortal.Models;

public sealed class TaskSliceApplyResult
{
    public bool Success { get; set; }
    public bool RolledBack { get; set; }
    public bool RollbackSucceeded { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? RollbackMessage { get; set; }
    public string? BackupFolderPath { get; set; }
    public int BackedUpFilesCount { get; set; }
    public int AppliedFilesCount { get; set; }
    public TaskSliceBuildVerificationResult? BuildVerificationResult { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = [];
    public string? RiskGateMessage { get; set; }
}
