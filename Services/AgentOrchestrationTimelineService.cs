using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentOrchestrationTimelineService(IWebHostEnvironment environment)
{
    private const string TimelineFileName = "agent-orchestration-timeline.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentOrchestrationTimelineEvent>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.TimestampUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentOrchestrationTimelineEvent>> GetByRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return [];
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.TimestampUtc)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationTimelineEvent> AddAsync(AgentOrchestrationTimelineEvent item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            var saved = Clone(item);

            if (string.IsNullOrWhiteSpace(saved.Id))
            {
                saved.Id = Guid.NewGuid().ToString("N");
            }

            if (saved.TimestampUtc == default)
            {
                saved.TimestampUtc = DateTime.UtcNow;
            }

            items.Add(saved);
            await SaveAsync(items, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<AgentOrchestrationTimelineEvent>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetTimelinePath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentOrchestrationTimelineEvent>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentOrchestrationTimelineEvent> items, CancellationToken cancellationToken)
    {
        var path = GetTimelinePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetTimelinePath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", TimelineFileName);
    }

    private static AgentOrchestrationTimelineEvent Clone(AgentOrchestrationTimelineEvent item)
    {
        return new AgentOrchestrationTimelineEvent
        {
            Id = item.Id,
            RunId = item.RunId,
            TimestampUtc = item.TimestampUtc,
            EventType = item.EventType,
            StepName = item.StepName,
            Message = item.Message,
            Severity = item.Severity,
            RelatedEntityId = item.RelatedEntityId
        };
    }
}
