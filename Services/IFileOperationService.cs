using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IFileOperationService
{
    Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileOperationResult>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileOperationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<FileOperationResult?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}
