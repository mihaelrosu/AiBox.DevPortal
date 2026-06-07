using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IExecutionPermissionProfileService
{
    Task<IReadOnlyList<ExecutionPermissionProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ExecutionPermissionProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ExecutionPermissionProfile> AddAsync(ExecutionPermissionProfile profile, CancellationToken cancellationToken = default);
    Task<ExecutionPermissionProfile?> UpdateAsync(string id, ExecutionPermissionProfile profile, CancellationToken cancellationToken = default);
    Task<ExecutionPermissionProfile?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
