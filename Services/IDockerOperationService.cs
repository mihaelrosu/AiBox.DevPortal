using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IDockerOperationService
{
    Task<DockerOperationResult> ExecuteAsync(DockerOperationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerOperationResult>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DockerOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<DockerOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<DockerOperationResult?> GetLatestForStepAsync(string runId, string stepId, CancellationToken cancellationToken = default);
}
