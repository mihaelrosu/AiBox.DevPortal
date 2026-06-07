using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class ExecutionPermissionProfileEndpoints
{
    public static IEndpointRouteBuilder MapExecutionPermissionProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/execution-permission-profiles")
            .WithTags("Execution Permission Profiles");

        group.MapGet("/", async (IExecutionPermissionProfileService registry, CancellationToken cancellationToken) =>
                Results.Ok(await registry.GetAllAsync(cancellationToken)))
            .WithName("GetExecutionPermissionProfiles")
            .WithSummary("Lists execution permission profiles.");

        group.MapGet("/{id}", async (string id, IExecutionPermissionProfileService registry, CancellationToken cancellationToken) =>
            {
                var profile = await registry.GetByIdAsync(id, cancellationToken);
                return profile is null ? Results.NotFound() : Results.Ok(profile);
            })
            .WithName("GetExecutionPermissionProfile")
            .WithSummary("Gets an execution permission profile.");

        group.MapPost("/", async (ExecutionPermissionProfile profile, IExecutionPermissionProfileService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var created = await registry.AddAsync(profile, cancellationToken);
                    return Results.Created($"/api/execution-permission-profiles/{created.Id}", created);
                }))
            .WithName("CreateExecutionPermissionProfile")
            .WithSummary("Creates an execution permission profile.");

        group.MapPut("/{id}", async (string id, ExecutionPermissionProfile profile, IExecutionPermissionProfileService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var updated = await registry.UpdateAsync(id, profile, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }))
            .WithName("UpdateExecutionPermissionProfile")
            .WithSummary("Updates an execution permission profile.");

        group.MapDelete("/{id}", async (string id, IExecutionPermissionProfileService registry, CancellationToken cancellationToken) =>
                await registry.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteExecutionPermissionProfile")
            .WithSummary("Deletes an execution permission profile.");

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
