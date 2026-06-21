using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ScheduledAgentExecutionService(IWebHostEnvironment environment)
{
    private const string ExecutionsFileName = "scheduled-agent-executions.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<ScheduledAgentExecution> CreateAsync(ScheduledAgentExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var saved = Clone(execution);
        saved.ScheduledRunId = saved.ScheduledRunId.Trim();
        if (string.IsNullOrWhiteSpace(saved.ScheduledRunId))
        {
            throw new ArgumentException("Scheduled run id is required.");
        }

        saved.ScheduleName = saved.ScheduleName.Trim();
        if (string.IsNullOrWhiteSpace(saved.ScheduleName))
        {
            throw new ArgumentException("Schedule name is required.");
        }

        saved.TaskName = saved.TaskName.Trim();
        if (string.IsNullOrWhiteSpace(saved.TaskName))
        {
            throw new ArgumentException("Task name is required.");
        }

        if (saved.StartedUtc == default)
        {
            saved.StartedUtc = DateTimeOffset.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(saved.Error))
        {
            saved.Error = saved.Error.Trim();
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var executions = await LoadAsync(cancellationToken);
            executions.Add(saved);
            await SaveAsync(executions, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledAgentExecution>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderBy(item => item.StartedUtc)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledAgentExecution>> GetByRunIdAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return [];
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .Where(item => item.ScheduledRunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.StartedUtc)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<ScheduledAgentExecution>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetExecutionsPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ScheduledAgentExecution>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<ScheduledAgentExecution> executions, CancellationToken cancellationToken)
    {
        var path = GetExecutionsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, executions, JsonOptions, cancellationToken);
    }

    private string GetExecutionsPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", ExecutionsFileName);
    }

    private static ScheduledAgentExecution Clone(ScheduledAgentExecution execution)
    {
        return new ScheduledAgentExecution
        {
            Id = execution.Id,
            ScheduledRunId = execution.ScheduledRunId,
            ScheduleName = execution.ScheduleName,
            TaskName = execution.TaskName,
            StartedUtc = execution.StartedUtc,
            CompletedUtc = execution.CompletedUtc,
            Success = execution.Success,
            Error = execution.Error,
            DurationMs = execution.DurationMs
        };
    }
}
