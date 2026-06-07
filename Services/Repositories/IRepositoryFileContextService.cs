using AiBox.DevPortal.Models.Repositories;

namespace AiBox.DevPortal.Services.Repositories;

public interface IRepositoryFileContextService
{
    Task<IReadOnlyList<RepositoryFileContent>> ReadAsync(
        string repositoryPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);
}
