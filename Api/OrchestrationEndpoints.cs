using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class OrchestrationEndpoints
{
    public static IEndpointRouteBuilder MapOrchestrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/orchestration/dashboard", async (
                IOrchestrationDashboardService dashboard,
                CancellationToken cancellationToken) =>
                Results.Ok(await dashboard.GetDashboardAsync(cancellationToken)))
            .WithName("GetOrchestrationDashboard")
            .WithTags("Orchestration")
            .WithSummary("Gets the multi-agent orchestration dashboard.");

        return endpoints;
    }
}
