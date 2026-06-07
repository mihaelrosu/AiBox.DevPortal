namespace AiBox.DevPortal.Models;

public sealed class VerificationCheck
{
    public string Name { get; set; } = string.Empty;
    public VerificationStatus Status { get; set; } = VerificationStatus.NotChecked;
    public VerificationSeverity Severity { get; set; } = VerificationSeverity.Info;
    public string Message { get; set; } = string.Empty;
}
