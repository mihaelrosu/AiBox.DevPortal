using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IGeneratedImageHistoryService
{
    Task<IReadOnlyList<GeneratedImage>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<GeneratedImage?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<GeneratedImage> SaveAsync(SdxlTextToImageResult result, CancellationToken cancellationToken = default);
}
