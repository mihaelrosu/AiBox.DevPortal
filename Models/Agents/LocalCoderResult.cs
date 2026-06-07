namespace AiBox.DevPortal.Models.Agents;

public sealed class LocalCoderResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
