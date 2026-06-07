using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class ComfyUiOperationEndpoints
{
    public static IEndpointRouteBuilder MapComfyUiOperationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/comfyui/operation", async (
                ComfyUiOperationRequest request,
                IComfyUiOperationService comfyUiOperations,
                CancellationToken cancellationToken) =>
                Results.Ok(await comfyUiOperations.ExecuteAsync(request, cancellationToken)))
            .WithName("ExecuteComfyUiOperation")
            .WithTags("ComfyUI")
            .WithSummary("Runs a controlled ComfyUI operation for a workflow step.");

        return endpoints;
    }
}
