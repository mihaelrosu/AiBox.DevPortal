using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class WorkflowTemplateEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workflow-templates")
            .WithTags("Workflow Templates");

        group.MapGet("/", async (IWorkflowTemplateService templates, CancellationToken cancellationToken) =>
                Results.Ok(await templates.GetAllAsync(cancellationToken)))
            .WithName("GetWorkflowTemplates")
            .WithSummary("Lists reusable workflow templates.");

        group.MapGet("/{id}", async (
                    string id,
                    IWorkflowTemplateService templates,
                    CancellationToken cancellationToken) =>
                await templates.GetByIdAsync(id, cancellationToken) is { } template
                    ? Results.Ok(template)
                    : Results.NotFound())
            .WithName("GetWorkflowTemplate")
            .WithSummary("Gets a reusable workflow template.");

        group.MapPost("/{id}/create-workflow", async (
                    string id,
                    IWorkflowTemplateService templates,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var workflow = await templates.CreateWorkflowAsync(id, cancellationToken);
                    return workflow is null
                        ? Results.NotFound()
                        : Results.Created($"/api/workflows/{workflow.Id}", workflow);
                }))
            .WithName("CreateWorkflowFromTemplate")
            .WithSummary("Creates and saves a new workflow from a reusable template.");

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
