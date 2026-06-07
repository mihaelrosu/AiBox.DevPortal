using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderService
{
    Task<LocalCoderResult> CreatePlanAsync(LocalCoderTask task);
    Task<LocalCoderResult> GeneratePatchAsync(LocalCoderTask task);
    Task<LocalCoderResult> RunBuildAsync(LocalCoderTask task);
    Task<LocalCoderResult> ReviewAsync(LocalCoderTask task);
    Task<LocalCoderResult> ApplyPatchAsync(LocalCoderTask task);
}
