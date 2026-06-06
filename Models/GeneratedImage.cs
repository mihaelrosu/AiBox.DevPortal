namespace AiBox.DevPortal.Models;

public sealed class GeneratedImage
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; }
    public long Seed { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Steps { get; set; }
    public double CFG { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
}
