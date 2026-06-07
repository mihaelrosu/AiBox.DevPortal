using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderHistoryService
{
    Task<IReadOnlyList<LocalCoderTaskHistoryRecord>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LocalCoderTaskHistoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<LocalCoderTaskHistoryRecord> SaveAsync(LocalCoderTaskHistoryRecord record, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
