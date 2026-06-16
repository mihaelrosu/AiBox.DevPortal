namespace AiBox.DevPortal.Models;

public sealed class TaskSliceExecutionResult
{
    public string PlanId { get; set; } = string.Empty;
    public string SliceId { get; set; } = string.Empty;
    public string SliceTitle { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public string PatchPackageId { get; set; } = string.Empty;
    public string BackupId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public bool BuildSuccess { get; set; }
    public bool VerificationSuccess { get; set; }
    public List<string> AppliedFiles { get; set; } = [];
    public DateTime? AppliedAt { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<string> GeneratedFiles { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}
