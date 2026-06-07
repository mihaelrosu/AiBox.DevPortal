using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderPatchService
{
    Task<LocalCoderResult> GeneratePatchAsync(LocalCoderTask task, string planText, CancellationToken cancellationToken = default);
    LocalCoderPatchValidationResult ValidatePatch(LocalCoderTask task, string patchText);
}
