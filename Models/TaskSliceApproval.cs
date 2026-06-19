namespace AiBox.DevPortal.Models;

public enum TaskSliceApprovalStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2
}

public sealed class TaskSliceApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public TaskSliceApprovalStatus Status { get; set; } = TaskSliceApprovalStatus.PendingApproval;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }
}
