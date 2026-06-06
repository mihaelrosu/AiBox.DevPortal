namespace AiBox.DevPortal.Models;

public sealed class PromptEnhanceRequest
{
    public string PositivePrompt { get; set; } = string.Empty;
    public string? Style { get; set; }
}
