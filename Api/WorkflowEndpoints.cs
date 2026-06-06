using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workflows")
            .WithTags("Workflows");

        group.MapGet("/", async (IWorkflowRegistryService registry, CancellationToken cancellationToken) =>
                Results.Ok(await registry.GetAllAsync(cancellationToken)))
            .WithName("GetWorkflows")
            .WithSummary("Lists configured workflows.");

        group.MapPost("/preview", async (WorkflowRunPreviewRequest request, IWorkflowRunPreviewService previewService, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var preview = await previewService.PreviewAsync(request, cancellationToken);
                    return preview is null ? Results.NotFound() : Results.Ok(preview);
                }))
            .WithName("PreviewWorkflowRun")
            .WithSummary("Previews the ordered prompts for a configured workflow without executing it.");

        group.MapGet("/{id}", async (string id, IWorkflowRegistryService registry, CancellationToken cancellationToken) =>
            {
                var workflow = await registry.GetByIdAsync(id, cancellationToken);
                return workflow is null ? Results.NotFound() : Results.Ok(workflow);
            })
            .WithName("GetWorkflow")
            .WithSummary("Gets a configured workflow.");

        group.MapPost("/", async (WorkflowDefinition workflow, IWorkflowRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var created = await registry.AddAsync(workflow, cancellationToken);
                    return Results.Created($"/api/workflows/{created.Id}", created);
                }))
            .WithName("CreateWorkflow")
            .WithSummary("Creates a workflow configuration.");

        group.MapPut("/{id}", async (string id, WorkflowDefinition workflow, IWorkflowRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var updated = await registry.UpdateAsync(id, workflow, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }))
            .WithName("UpdateWorkflow")
            .WithSummary("Updates a workflow configuration.");

        group.MapDelete("/{id}", async (string id, IWorkflowRegistryService registry, CancellationToken cancellationToken) =>
                await registry.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteWorkflow")
            .WithSummary("Deletes a workflow configuration.");

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ArgumentException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: exception.Message);
        }
    }
}
