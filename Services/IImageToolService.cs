using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IImageToolService
{
    Task<ComfyUiGenerationResult> TextToImageAsync(TextToImageToolRequest request, CancellationToken cancellationToken = default);
    Task<ComfyUiGenerationResult> ImageToImageAsync(ImageToImageToolRequest request, string fileName, Stream image, CancellationToken cancellationToken = default);
    Task<ComfyUiGenerationResult> FaceDetailerAsync(FaceDetailerToolRequest request, string fileName, Stream image, CancellationToken cancellationToken = default);
    Task<ComfyUiGenerationResult> UpscaleAsync(UpscaleToolRequest request, string fileName, Stream image, CancellationToken cancellationToken = default);
    Task<ComfyUiGenerationResult> LoraAsync(LoraToolRequest request, CancellationToken cancellationToken = default);
}
