using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class GitEndpoints
{
    public static IEndpointRouteBuilder MapGitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/git")
            .WithTags("Git");

        group.MapPost("/operation", async (GitOperationRequest request, IGitOperationService git, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(await git.ExecuteAsync(request, cancellationToken))))
            .WithName("ExecuteGitOperation")
            .WithSummary("Runs a safe Git operation through the permission layer.");

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
        catch (OperationCanceledException exception)
        {
            return Results.Problem(statusCode: 499, detail: exception.Message);
        }
    }
}
