using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IOllamaService
{
    Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default);
}
