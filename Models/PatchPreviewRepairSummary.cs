namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewRepairSummary
{
    public string OriginalOperation { get; set; } = string.Empty;
    public string RepairAttempt { get; set; } = string.Empty;
    public string RepairResult { get; set; } = string.Empty;
    public string ValidationError { get; set; } = string.Empty;
}
