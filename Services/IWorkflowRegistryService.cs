using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IWorkflowRegistryService
{
    Task<IReadOnlyList<WorkflowDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition> AddAsync(WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> UpdateAsync(string id, WorkflowDefinition workflow, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
