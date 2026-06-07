using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class DockerOperationEndpoints
{
    public static IEndpointRouteBuilder MapDockerOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/docker/operation", async (
                DockerOperationRequest request,
                IDockerOperationService docker,
                CancellationToken cancellationToken) =>
                Results.Ok(await docker.ExecuteAsync(request, cancellationToken)))
            .WithName("ExecuteDockerOperation")
            .WithTags("Docker")
            .WithSummary("Runs a controlled Docker operation for a workflow step.");

        return endpoints;
    }
}
