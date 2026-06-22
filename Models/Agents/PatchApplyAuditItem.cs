namespace AiBox.DevPortal.Models.Agents;

public sealed class PatchApplyAuditItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = string.Empty;
    public string PatchPreviewId { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public string PolicyId { get; set; } = string.Empty;
    public string ModelRouteId { get; set; } = string.Empty;
    public string SnapshotId { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset? ApprovedAt { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> ChangedFiles { get; set; } = [];
    public bool VerificationSucceeded { get; set; }
    public string VerificationSummary { get; set; } = string.Empty;
    public bool RestoreAttempted { get; set; }
    public bool RestoreSucceeded { get; set; }
    public string RestoreSummary { get; set; } = string.Empty;
}
