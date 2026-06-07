using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IOrchestrationDashboardService
{
    Task<OrchestrationDashboardResult> GetDashboardAsync(CancellationToken cancellationToken = default);
}
