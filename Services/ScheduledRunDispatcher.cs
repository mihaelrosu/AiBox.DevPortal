using AiBox.DevPortal.Models;

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
                    StartedUtc = nowUtc,
                    Success = true
                }, cancellationToken);

                var completed = await scheduledRunService.MarkCompletedAsync(
                    run.Id,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                if (completed is null)
                {
                    continue;
                }

                await scheduledExecutionService.MarkCompletedAsync(
                    execution!.Id,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                _logger.LogInformation("Scheduled run {RunId} completed.", run.Id);
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
}
