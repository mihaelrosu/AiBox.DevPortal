using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IVerificationService
{
    Task<VerificationResult> VerifyAsync(string runId, VerificationRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VerificationResult>> GetForRunAsync(string runId, CancellationToken cancellationToken = default);
    Task<VerificationResult?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken = default);
}
