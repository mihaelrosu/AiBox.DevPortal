using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IExecutionEngineService
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExecutionResult>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ExecutionResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<ExecutionResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default);
}
