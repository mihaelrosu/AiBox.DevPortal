namespace AiBox.DevPortal.Models;

public sealed class PatchRollbackEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string BackupId { get; set; } = string.Empty;
    public IReadOnlyList<string> ChangedFiles { get; set; } = [];
    public bool Restored { get; set; }
    public DateTime? RestoreTimestampUtc { get; set; }
}
