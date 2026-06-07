using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IGitOperationService
{
    Task<GitOperationResult> ExecuteAsync(GitOperationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitOperationResult>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<GitOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<GitOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default);
}
