using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public interface IPatchPackageService
{
    Task<IReadOnlyList<PatchPackage>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PatchPackage?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<PatchPackage> CreateFromPreviewAsync(LocalCoderPatchPreview preview, string userRequest, CancellationToken cancellationToken = default);
    Task<PatchPackage> SaveAsync(PatchPackage package, CancellationToken cancellationToken = default);
    Task<PatchPackage?> UpdateStatusAsync(string id, PatchPackageStatus status, string? statusMessage = null, CancellationToken cancellationToken = default);
    Task<PatchPackage?> ApproveAsync(string id, CancellationToken cancellationToken = default);
}
