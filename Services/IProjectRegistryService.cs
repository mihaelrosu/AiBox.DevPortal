using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IProjectRegistryService
{
    Task<IReadOnlyList<ProjectDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProjectDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ProjectDefinition> AddAsync(ProjectDefinition project, CancellationToken cancellationToken = default);
    Task<ProjectDefinition?> UpdateAsync(string id, ProjectDefinition project, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
