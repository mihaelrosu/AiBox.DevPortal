namespace AiBox.DevPortal.Models;

public enum HumanApprovalRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Expired
}

public sealed class HumanApprovalRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;
    public string RequestedBy { get; set; } = string.Empty;
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public string Reason { get; set; } = string.Empty;
    public HumanApprovalRequestStatus Status { get; set; } = HumanApprovalRequestStatus.Pending;
    public DateTime? DecisionAtUtc { get; set; }
    public string DecisionBy { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}
