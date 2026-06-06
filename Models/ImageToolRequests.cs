namespace AiBox.DevPortal.Models;

public sealed class TextToImageToolRequest
{
    public string Model { get; set; } = "sdxl";
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public int Steps { get; set; } = 30;
    public double Cfg { get; set; } = 7;
    public string Sampler { get; set; } = "dpmpp_2m";
    public string Scheduler { get; set; } = "karras";
    public long Seed { get; set; } = -1;
}

public sealed class ImageToImageToolRequest
{
    public string Model { get; set; } = "sdxl";
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public int Steps { get; set; } = 20;
    public double Denoise { get; set; } = 0.55;
    public long Seed { get; set; } = -1;
}

public sealed class FaceDetailerToolRequest
{
    public string Prompt { get; set; } = "natural detailed face, realistic skin texture, sharp eyes, balanced lighting";
    public string NegativePrompt { get; set; } = "low quality, blurry, distorted face, bad anatomy, watermark, text";
    public double Denoise { get; set; } = 0.35;
    public long Seed { get; set; } = -1;
}

public sealed class UpscaleToolRequest
{
    public string Upscaler { get; set; } = string.Empty;
    public int Scale { get; set; } = 2;
}

public sealed class LoraToolRequest
{
    public string Model { get; set; } = "sdxl";
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string Lora { get; set; } = string.Empty;
    public double Strength { get; set; } = 0.8;
    public long Seed { get; set; } = -1;
}
