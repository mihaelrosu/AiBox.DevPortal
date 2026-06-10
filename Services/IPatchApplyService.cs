using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchApplyService
{
    Task<PatchPackage> ApplyAsync(string patchPackageId, CancellationToken cancellationToken = default);
}
