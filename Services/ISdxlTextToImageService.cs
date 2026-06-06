using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ISdxlTextToImageService
{
    Task<SdxlTextToImageResult> GenerateAsync(SdxlTextToImageRequest request);
}
