using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchApprovalGateService
{
    Task<IReadOnlyList<PatchApprovalGateResult>> EvaluateAsync(PatchPackage package, CancellationToken cancellationToken = default);
}
