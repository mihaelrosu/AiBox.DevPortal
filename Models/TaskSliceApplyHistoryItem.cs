namespace AiBox.DevPortal.Models;

public sealed class TaskSliceApplyHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public string PatchPackageId { get; set; } = string.Empty;
    public DateTime AppliedAtUtc { get; set; } = DateTime.UtcNow;
    public string? BackupFolderPath { get; set; }
    public int BackedUpFilesCount { get; set; }
    public int AppliedFilesCount { get; set; }
    public bool BuildSucceeded { get; set; }
    public bool RolledBack { get; set; }
    public bool RollbackSucceeded { get; set; }
    public string ResultMessage { get; set; } = string.Empty;
}
