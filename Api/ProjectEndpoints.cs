using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/projects")
            .WithTags("Projects");

        group.MapGet("/", async (IProjectRegistryService registry, CancellationToken cancellationToken) =>
                Results.Ok(await registry.GetAllAsync(cancellationToken)))
            .WithName("GetProjects")
            .WithSummary("Lists configured projects.");

        group.MapGet("/{id}", async (string id, IProjectRegistryService registry, CancellationToken cancellationToken) =>
            {
                var project = await registry.GetByIdAsync(id, cancellationToken);
                return project is null ? Results.NotFound() : Results.Ok(project);
            })
            .WithName("GetProject")
            .WithSummary("Gets a configured project.");

        group.MapPost("/", async (ProjectDefinition project, IProjectRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var created = await registry.AddAsync(project, cancellationToken);
                    return Results.Created($"/api/projects/{created.Id}", created);
                }))
            .WithName("CreateProject")
            .WithSummary("Creates a project configuration.");

        group.MapPut("/{id}", async (string id, ProjectDefinition project, IProjectRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var updated = await registry.UpdateAsync(id, project, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }))
            .WithName("UpdateProject")
            .WithSummary("Updates a project configuration.");

        group.MapDelete("/{id}", async (string id, IProjectRegistryService registry, CancellationToken cancellationToken) =>
                await registry.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteProject")
            .WithSummary("Deletes a project configuration.");

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
