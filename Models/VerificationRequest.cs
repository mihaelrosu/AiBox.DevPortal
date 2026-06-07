namespace AiBox.DevPortal.Models;

public sealed class VerificationRequest
{
    public string RunId { get; set; } = string.Empty;
    public bool IncludeLocalLlmReview { get; set; } = true;
}
