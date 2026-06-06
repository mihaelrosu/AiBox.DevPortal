using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IToolStatusService
{
    Task<IReadOnlyList<AiBoxToolLink>> GetToolsAsync(CancellationToken cancellationToken = default);
}
