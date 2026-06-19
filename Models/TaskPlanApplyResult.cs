namespace AiBox.DevPortal.Models;

public sealed class TaskPlanApplyResult
{
    public string PlanId { get; set; } = string.Empty;
    public string PlanTitle { get; set; } = string.Empty;
    public int TotalSlices { get; set; }
    public int AppliedSlices { get; set; }
    public string? FailedSliceId { get; set; }
    public bool RollbackPerformed { get; set; }
    public bool Success { get; set; }
    public string? CurrentSliceId { get; set; }
    public string? CurrentSliceTitle { get; set; }
    public int CurrentSliceIndex { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public IReadOnlyList<string> AuditTrail { get; set; } = [];
    public IReadOnlyList<TaskSliceApplyResult> SliceResults { get; set; } = [];
    public IReadOnlyList<string> ValidationErrors { get; set; } = [];
    public IReadOnlyList<string> CyclesDetected { get; set; } = [];
    public IReadOnlyList<string> OrderedSliceIds { get; set; } = [];
}
