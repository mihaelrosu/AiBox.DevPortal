using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IOllamaService
{
    Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default);
}
