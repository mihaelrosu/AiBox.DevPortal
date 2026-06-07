using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderBuildService
{
    Task<LocalCoderBuildResult> RunBuildAsync(LocalCoderTask task, CancellationToken cancellationToken = default);
}
