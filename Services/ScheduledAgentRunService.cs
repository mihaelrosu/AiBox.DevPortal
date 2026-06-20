using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ScheduledAgentRunService(
    IWebHostEnvironment environment,
    ExecutionPolicyProfileService executionPolicyProfileService)
{
    private const string SchedulesFileName = "scheduled-agent-runs.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<ScheduledAgentRun>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ScheduledAgentRun?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedule = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return schedule is null ? null : Clone(schedule);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ScheduledAgentRun> CreateAsync(ScheduledAgentRun schedule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var saved = await ValidateAndNormalizeAsync(schedule, cancellationToken);
        saved.Id = Guid.NewGuid().ToString("N");
        saved.CreatedAtUtc = saved.CreatedAtUtc == default ? DateTime.UtcNow : saved.CreatedAtUtc;

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await LoadAsync(cancellationToken);
            schedules.Add(saved);
            await SaveAsync(schedules, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<ScheduledAgentRun?> UpdateAsync(string id, ScheduledAgentRun schedule, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Schedule id is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(schedule);

        var updated = await ValidateAndNormalizeAsync(schedule, cancellationToken);
        updated.Id = id.Trim();

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await LoadAsync(cancellationToken);
            var index = schedules.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            updated.CreatedAtUtc = schedules[index].CreatedAtUtc;
            schedules[index] = updated;
            await SaveAsync(schedules, cancellationToken);
            return Clone(updated);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await LoadAsync(cancellationToken);
            var removed = schedules.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                await SaveAsync(schedules, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public Task<ScheduledAgentRun?> EnableAsync(string id, CancellationToken cancellationToken = default)
    {
        return SetEnabledAsync(id, enabled: true, cancellationToken);
    }

    public Task<ScheduledAgentRun?> DisableAsync(string id, CancellationToken cancellationToken = default)
    {
        return SetEnabledAsync(id, enabled: false, cancellationToken);
    }

    private async Task<ScheduledAgentRun?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Schedule id is required.", nameof(id));
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var schedules = await LoadAsync(cancellationToken);
            var index = schedules.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return null;
            }

            schedules[index].Enabled = enabled;
            await SaveAsync(schedules, cancellationToken);
            return Clone(schedules[index]);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<ScheduledAgentRun>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetSchedulesPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ScheduledAgentRun>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<ScheduledAgentRun> schedules, CancellationToken cancellationToken)
    {
        var path = GetSchedulesPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, schedules, JsonOptions, cancellationToken);
    }

    private string GetSchedulesPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", SchedulesFileName);
    }

    private async Task<ScheduledAgentRun> ValidateAndNormalizeAsync(ScheduledAgentRun schedule, CancellationToken cancellationToken)
    {
        var name = schedule.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Schedule name is required.");
        }

        var cronExpression = schedule.CronExpression?.Trim();
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            throw new ArgumentException("Cron expression cannot be empty.");
        }

        var taskName = schedule.TaskName?.Trim();
        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name is required.");
        }

        var userRequest = schedule.UserRequest?.Trim();
        if (string.IsNullOrWhiteSpace(userRequest))
        {
            throw new ArgumentException("User request is required.");
        }

        var executionPolicyName = schedule.ExecutionPolicyName?.Trim();
        if (string.IsNullOrWhiteSpace(executionPolicyName))
        {
            throw new ArgumentException("Execution policy name is required.");
        }

        var executionPolicy = await executionPolicyProfileService.GetByNameAsync(executionPolicyName, cancellationToken);
        if (executionPolicy is null)
        {
            throw new ArgumentException($"Execution policy '{executionPolicyName}' does not exist.");
        }

        return new ScheduledAgentRun
        {
            Name = name,
            Enabled = schedule.Enabled,
            CronExpression = cronExpression,
            TaskName = taskName,
            UserRequest = userRequest,
            ExecutionPolicyName = executionPolicy.Name,
            CommitAndSync = schedule.CommitAndSync,
            CreatedAtUtc = schedule.CreatedAtUtc == default ? DateTime.UtcNow : schedule.CreatedAtUtc,
            LastRunAtUtc = schedule.LastRunAtUtc,
            NextRunAtUtc = schedule.NextRunAtUtc
        };
    }

    private static ScheduledAgentRun Clone(ScheduledAgentRun schedule)
    {
        return new ScheduledAgentRun
        {
            Id = schedule.Id,
            Name = schedule.Name,
            Enabled = schedule.Enabled,
            CronExpression = schedule.CronExpression,
            TaskName = schedule.TaskName,
            UserRequest = schedule.UserRequest,
            ExecutionPolicyName = schedule.ExecutionPolicyName,
            CommitAndSync = schedule.CommitAndSync,
            CreatedAtUtc = schedule.CreatedAtUtc,
            LastRunAtUtc = schedule.LastRunAtUtc,
            NextRunAtUtc = schedule.NextRunAtUtc
        };
    }
}
