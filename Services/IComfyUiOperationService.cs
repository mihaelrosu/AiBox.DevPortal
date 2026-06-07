using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IComfyUiOperationService
{
    Task<ComfyUiOperationResult> ExecuteAsync(ComfyUiOperationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComfyUiOperationResult>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComfyUiOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<ComfyUiOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ComfyUiOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default);
}
