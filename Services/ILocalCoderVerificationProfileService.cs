using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ILocalCoderVerificationProfileService
{
    Task<IReadOnlyList<LocalCoderVerificationProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<LocalCoderVerificationProfile?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
}
