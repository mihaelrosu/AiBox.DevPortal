namespace AiBox.DevPortal.Models;

public sealed class TaskSliceApplyAuditEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public int RiskScore { get; set; }
    public bool ApprovedHighRiskApply { get; set; }
    public bool Success { get; set; }
    public bool Blocked { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> ChangedFiles { get; set; } = [];
}
