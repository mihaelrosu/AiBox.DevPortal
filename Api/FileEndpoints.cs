using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/files")
            .WithTags("Files");

        group.MapPost("/operation", async (FileOperationRequest request, IFileOperationService files, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var result = await files.ExecuteAsync(request, cancellationToken);
                    return Results.Ok(result);
                }))
            .WithName("ExecuteFileOperation")
            .WithSummary("Runs a safe file operation through the permission layer.");

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
