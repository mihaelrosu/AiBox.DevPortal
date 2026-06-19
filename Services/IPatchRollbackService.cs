using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchRollbackService
{
    Task<PatchPackage> RollbackAsync(string patchPackageId, CancellationToken cancellationToken = default);
    Task<PatchRollbackEntry> RecordAsync(PatchRollbackEntry entry, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PatchRollbackEntry>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default);
}
