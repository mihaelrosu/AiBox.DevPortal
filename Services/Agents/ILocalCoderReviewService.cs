using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderReviewService
{
    Task<LocalCoderResult> ReviewAsync(
        LocalCoderTask task,
        string planText,
        string patchText,
        LocalCoderPatchValidationResult patchValidation,
        string buildOutput,
        CancellationToken cancellationToken = default);
}
