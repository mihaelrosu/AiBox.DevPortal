namespace AiBox.DevPortal.Models;

public sealed class SdxlTextToImageRequest
{
    public string PositivePrompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 30;
    public double CfgScale { get; set; } = 7;
    public long Seed { get; set; } = -1;
    public string CheckpointName { get; set; } = "sd_xl_base_1.0.safetensors";
}
