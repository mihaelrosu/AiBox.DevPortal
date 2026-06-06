using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Api;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agents")
            .WithTags("Agents");

        group.MapGet("/", async (IAgentRegistryService registry, CancellationToken cancellationToken) =>
                Results.Ok(await registry.GetAllAsync(cancellationToken)))
            .WithName("GetAgents")
            .WithSummary("Lists registered agents.");

        group.MapGet("/{id}", async (string id, IAgentRegistryService registry, CancellationToken cancellationToken) =>
            {
                var agent = await registry.GetByIdAsync(id, cancellationToken);
                return agent is null ? Results.NotFound() : Results.Ok(agent);
            })
            .WithName("GetAgent")
            .WithSummary("Gets a registered agent.");

        group.MapPost("/", async (AgentDefinition agent, IAgentRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var created = await registry.AddAsync(agent, cancellationToken);
                    return Results.Created($"/api/agents/{created.Id}", created);
                }))
            .WithName("CreateAgent")
            .WithSummary("Registers an agent.");

        group.MapPut("/{id}", async (string id, AgentDefinition agent, IAgentRegistryService registry, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var updated = await registry.UpdateAsync(id, agent, cancellationToken);
                    return updated is null ? Results.NotFound() : Results.Ok(updated);
                }))
            .WithName("UpdateAgent")
            .WithSummary("Updates a registered agent.");

        group.MapPatch("/{id}/enabled", async (string id, SetAgentEnabledRequest request, IAgentRegistryService registry, CancellationToken cancellationToken) =>
            {
                var updated = await registry.SetEnabledAsync(id, request.Enabled, cancellationToken);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            })
            .WithName("SetAgentEnabled")
            .WithSummary("Enables or disables a registered agent.");

        group.MapDelete("/{id}", async (string id, IAgentRegistryService registry, CancellationToken cancellationToken) =>
                await registry.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteAgent")
            .WithSummary("Deletes a registered agent.");

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

    public sealed record SetAgentEnabledRequest(bool Enabled);
}
