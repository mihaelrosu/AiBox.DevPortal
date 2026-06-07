using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/execution")
            .WithTags("Execution");

        group.MapPost("/run", async (ExecutionRequest request, IExecutionEngineService engine, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var result = await engine.ExecuteAsync(request, cancellationToken);
                    return Results.Ok(result);
                }))
            .WithName("RunExecutionCommand")
            .WithSummary("Runs a command through the safe local execution engine.");

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
        catch (InvalidOperationException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: exception.Message);
        }
        catch (TaskCanceledException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, detail: exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            return Results.Problem(statusCode: 499, detail: exception.Message);
        }
    }
}
