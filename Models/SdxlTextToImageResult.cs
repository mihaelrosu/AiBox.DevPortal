namespace AiBox.DevPortal.Models;

public sealed class SdxlTextToImageResult
{
    public string PromptId { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public long Seed { get; set; }
    public string Model { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Steps { get; set; }
    public double Cfg { get; set; }
    public string Sampler { get; set; } = string.Empty;
    public string Scheduler { get; set; } = string.Empty;
    public string PositivePrompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
}
