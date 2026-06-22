using AiBox.DevPortal.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AiBox.DevPortal.Services;

public sealed class ScheduledRunDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledRunDispatcher> _logger;

    public ScheduledRunDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledRunDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessDueRunsAsync(stoppingToken);

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    private async Task ProcessDueRunsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var scheduledRunService = scope.ServiceProvider.GetRequiredService<ScheduledAgentRunService>();
        var scheduledExecutionService = scope.ServiceProvider.GetRequiredService<ScheduledAgentExecutionService>();
        var orchestrationService = scope.ServiceProvider.GetRequiredService<AgentOrchestrationService>();

        var nowUtc = DateTimeOffset.UtcNow;
        var dueRuns = await scheduledRunService.GetDueRunsAsync(nowUtc, cancellationToken);

        foreach (var run in dueRuns)
        {
            if (run.IsRunning)
            {
                continue;
            }

            ScheduledAgentExecution? execution = null;

            try
            {
                var started = await scheduledRunService.MarkStartedAsync(run.Id, nowUtc, cancellationToken);
                if (started is null)
                {
                    continue;
                }

                _logger.LogInformation("Scheduled run {RunId} started.", run.Id);

                execution = await scheduledExecutionService.CreateAsync(new ScheduledAgentExecution
                {
                    ScheduledRunId = run.Id,
                    ScheduleName = run.Name,
                    TaskName = run.TaskName,
                    StartedUtc = started.LastStartedUtc ?? nowUtc
                }, cancellationToken);

                var executionPolicy = await orchestrationService.GetExecutionPolicyProfileAsync(run.ExecutionPolicyName, cancellationToken);
                if (executionPolicy is null)
                {
                    throw new InvalidOperationException($"Execution policy '{run.ExecutionPolicyName}' was not found.");
                }

                var orchestrationResult = await orchestrationService.RunOrchestrationAsync(
                    run.TaskName,
                    run.UserRequest,
                    run.CommitAndSync,
                    approveHighRiskApply: true,
                    cancellationToken);

                var completedAtUtc = orchestrationResult.CompletedAtUtc is null
                    ? DateTimeOffset.UtcNow
                    : new DateTimeOffset(orchestrationResult.CompletedAtUtc.Value, TimeSpan.Zero);

                if (orchestrationResult.Status == AgentOrchestrationStatus.Completed)
                {
                    var completed = await scheduledRunService.MarkCompletedAsync(
                        run.Id,
                        completedAtUtc,
                        cancellationToken);
                    if (completed is null)
                    {
                        continue;
                    }

                    await scheduledExecutionService.MarkCompletedAsync(
                        execution!.Id,
                        completedAtUtc,
                        cancellationToken);

                    _logger.LogInformation("Scheduled run {RunId} completed.", run.Id);
                    continue;
                }

                var failureMessage = GetFailureMessage(orchestrationResult);
                var failed = await scheduledRunService.MarkFailedAsync(
                    run.Id,
                    failureMessage,
                    completedAtUtc,
                    cancellationToken);
                if (failed is null)
                {
                    continue;
                }

                await scheduledExecutionService.MarkFailedAsync(
                    execution!.Id,
                    failureMessage,
                    completedAtUtc,
                    cancellationToken);

                _logger.LogWarning("Scheduled run {RunId} failed: {FailureMessage}", run.Id, failureMessage);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Scheduled run {RunId} failed during dispatch.", run.Id);

                try
                {
                    await scheduledRunService.MarkFailedAsync(
                        run.Id,
                        exception.Message,
                        DateTimeOffset.UtcNow,
                        cancellationToken);
                }
                catch (Exception markFailureException)
                {
                    _logger.LogError(markFailureException, "Failed to record failure for scheduled run {RunId}.", run.Id);
                }

                if (execution is not null)
                {
                    try
                    {
                        await scheduledExecutionService.MarkFailedAsync(
                            execution!.Id,
                            exception.Message,
                            DateTimeOffset.UtcNow,
                            cancellationToken);
                    }
                    catch (Exception markExecutionFailureException)
                    {
                        _logger.LogError(markExecutionFailureException, "Failed to record execution failure for scheduled run {RunId}.", run.Id);
                    }
                }
            }
        }
    }

    private static string GetFailureMessage(AgentOrchestrationRun run)
    {
        var failedStep = run.Steps.LastOrDefault(step => step.Status == AgentOrchestrationStatus.Failed);
        var stepMessage = failedStep?.ErrorMessage;

        return string.IsNullOrWhiteSpace(stepMessage)
            ? (!string.IsNullOrWhiteSpace(run.ApplyMessage)
                ? run.ApplyMessage
                : !string.IsNullOrWhiteSpace(run.GitMessage)
                    ? run.GitMessage
                    : !string.IsNullOrWhiteSpace(run.SafetySummary)
                        ? run.SafetySummary
                        : "Scheduled orchestration failed.")
            : stepMessage;
    }
}
