using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ILocalCoderHistoryService
{
    Task<IReadOnlyList<LocalCoderHistoryEntry>> GetHistoryAsync(int take = 50);
    Task<LocalCoderHistoryEntry?> GetEntryAsync(string id);
    Task AddEntryAsync(LocalCoderHistoryEntry entry);
    Task ClearHistoryAsync();
}
