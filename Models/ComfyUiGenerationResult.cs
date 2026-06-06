namespace AiBox.DevPortal.Models;

public sealed class ComfyUiGenerationResult
{
    public string PromptId { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public long Seed { get; set; }
}
