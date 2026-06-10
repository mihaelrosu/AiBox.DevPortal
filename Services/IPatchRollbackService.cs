using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchRollbackService
{
    Task<PatchPackage> RollbackAsync(string patchPackageId, CancellationToken cancellationToken = default);
}
