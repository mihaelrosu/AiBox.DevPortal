using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IWorkflowRunHistoryService
{
    Task<IReadOnlyList<WorkflowRunRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> CreateAsync(WorkflowRunPreviewRequest request, CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> UpdateStepResultAsync(
        string runId,
        string stepId,
        WorkflowRunStepResultUpdateRequest request,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> UpdateStepStatusAsync(
        string runId,
        string stepId,
        WorkflowRunStepStatus status,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> AppendStepNotesAsync(
        string runId,
        string stepId,
        string notes,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> ExecuteStepWithLocalLlmAsync(
        string runId,
        string stepId,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> ExecuteAllWithLocalLlmAsync(
        string runId,
        CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> PauseAsync(string runId, CancellationToken cancellationToken = default);
    Task<WorkflowRunRecord?> CancelAsync(string runId, CancellationToken cancellationToken = default);
    Task<CodexTaskExportResult?> ExportCodexTaskAsync(
        string runId,
        CodexTaskExportRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
