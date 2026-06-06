using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IWorkflowTemplateService
{
    Task<IReadOnlyList<WorkflowTemplateDefinition>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<WorkflowTemplateDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<WorkflowDefinition?> CreateWorkflowAsync(string templateId, CancellationToken cancellationToken = default);
}
