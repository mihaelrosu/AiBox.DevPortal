using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class WorkflowRunEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowRunEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workflow-runs")
            .WithTags("Workflow Runs");

        group.MapGet("/", async (IWorkflowRunHistoryService history, CancellationToken cancellationToken) =>
                Results.Ok(await history.GetAllAsync(cancellationToken)))
            .WithName("GetWorkflowRuns")
            .WithSummary("Lists planned workflow runs.");

        group.MapGet("/{id}", async (string id, IWorkflowRunHistoryService history, CancellationToken cancellationToken) =>
            {
                var run = await history.GetByIdAsync(id, cancellationToken);
                return run is null ? Results.NotFound() : Results.Ok(run);
            })
            .WithName("GetWorkflowRun")
            .WithSummary("Gets a planned workflow run.");

        group.MapPost("/", async (WorkflowRunPreviewRequest request, IWorkflowRunHistoryService history, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var run = await history.CreateAsync(request, cancellationToken);
                    return run is null ? Results.NotFound() : Results.Created($"/api/workflow-runs/{run.Id}", run);
                }))
            .WithName("CreateWorkflowRun")
            .WithSummary("Saves a planned workflow run without executing it.");

        group.MapPut("/{runId}/steps/{stepId}/result", async (
                    string runId,
                    string stepId,
                    WorkflowRunStepResultUpdateRequest request,
                    IWorkflowRunHistoryService history,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var run = await history.UpdateStepResultAsync(runId, stepId, request, cancellationToken);
                    return run is null ? Results.NotFound() : Results.Ok(run);
                }))
            .WithName("UpdateWorkflowRunStepResult")
            .WithSummary("Stores a manual step result and marks the step completed.");

        group.MapPut("/{runId}/steps/{stepId}/status", async (
                    string runId,
                    string stepId,
                    WorkflowRunStepStatusUpdateRequest request,
                    IWorkflowRunHistoryService history,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var run = await history.UpdateStepStatusAsync(runId, stepId, request.Status, cancellationToken);
                    return run is null ? Results.NotFound() : Results.Ok(run);
                }))
            .WithName("UpdateWorkflowRunStepStatus")
            .WithSummary("Updates a manual workflow run step status.");

        group.MapPost("/{runId}/steps/{stepId}/execute-llm", async (
                    string runId,
                    string stepId,
                    IWorkflowRunHistoryService history,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var run = await history.ExecuteStepWithLocalLlmAsync(runId, stepId, cancellationToken);
                    return run is null ? Results.NotFound() : Results.Ok(run);
                }))
            .WithName("ExecuteWorkflowRunStepWithLocalLlm")
            .WithSummary("Sends one workflow step prompt to local Ollama and stores the text response.");

        group.MapPost("/{runId}/execute-llm", async (
                    string runId,
                    IWorkflowRunHistoryService history,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var run = await history.ExecuteAllWithLocalLlmAsync(runId, cancellationToken);
                    return run is null ? Results.NotFound() : Results.Ok(run);
                }))
            .WithName("ExecuteWorkflowRunWithLocalLlm")
            .WithSummary("Runs all enabled workflow steps sequentially through local Ollama.");

        group.MapPost("/{runId}/verify", async (
                    string runId,
                    VerificationRequest request,
                    IVerificationService verification,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () => Results.Ok(await verification.VerifyAsync(runId, request, cancellationToken))))
            .WithName("VerifyWorkflowRun")
            .WithSummary("Runs deterministic and optional local-LLM verification for a workflow run.");

        group.MapPost("/{runId}/export-codex-task", async (
                    string runId,
                    CodexTaskExportRequest request,
                    IWorkflowRunHistoryService history,
                    CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                {
                    var export = await history.ExportCodexTaskAsync(runId, request, cancellationToken);
                    return export is null ? Results.NotFound() : Results.Ok(export);
                }))
            .WithName("ExportCodexTask")
            .WithSummary("Exports workflow results into a Codex task.");

        group.MapDelete("/{id}", async (string id, IWorkflowRunHistoryService history, CancellationToken cancellationToken) =>
                await history.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("DeleteWorkflowRun")
            .WithSummary("Deletes a workflow run record.");

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
        catch (HttpRequestException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: exception.Message);
        }
        catch (TaskCanceledException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, detail: exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            return Results.Problem(statusCode: 499, detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, detail: exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: exception.Message);
        }
    }
}
