namespace AiBox.DevPortal.Models;

public sealed class PatchPreviewMetricsSnapshot
{
    public long Attempts { get; set; }
    public long SuccessfulPreviews { get; set; }
    public long FailedPreviews { get; set; }
    public long RepairedPreviews { get; set; }
}
