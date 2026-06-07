namespace AiBox.DevPortal.Models;

public sealed class VerificationResult
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public VerificationStatus Status { get; set; } = VerificationStatus.NotChecked;
    public List<VerificationCheck> Checks { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public string RecommendedNextAction { get; set; } = string.Empty;
    public string LocalLlmReview { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
