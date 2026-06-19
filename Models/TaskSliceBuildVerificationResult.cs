namespace AiBox.DevPortal.Models;

public sealed class TaskSliceBuildVerificationResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public DateTime VerifiedAtUtc { get; set; }
}
