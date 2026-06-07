using AiBox.DevPortal.Models.Repositories;

namespace AiBox.DevPortal.Services.Repositories;

public interface IRepositoryScannerService
{
    Task<RepositoryScanResult> ScanAsync(string repositoryPath, CancellationToken cancellationToken = default);
}
