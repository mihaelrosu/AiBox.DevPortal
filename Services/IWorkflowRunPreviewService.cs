using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IWorkflowRunPreviewService
{
    Task<WorkflowRunPreviewResult?> PreviewAsync(
        WorkflowRunPreviewRequest request,
        CancellationToken cancellationToken = default);
}
