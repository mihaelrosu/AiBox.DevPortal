using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentRunTimelineService(IWebHostEnvironment environment)
{
    private const string FileName = "agent-run-timeline.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentRunTimelineItem>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.Timestamp)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentRunTimelineItem>> GetByRunIdAsync(string runId, CancellationToken cancellationToken = default)
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
                .OrderBy(item => item.Timestamp)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentRunTimelineItem> AddAsync(AgentRunTimelineItem item, CancellationToken cancellationToken = default)
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

            if (saved.Timestamp == default)
            {
                saved.Timestamp = DateTimeOffset.UtcNow;
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

    private async Task<List<AgentRunTimelineItem>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentRunTimelineItem>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentRunTimelineItem> items, CancellationToken cancellationToken)
    {
        var path = GetPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", FileName);
    }

    private static AgentRunTimelineItem Clone(AgentRunTimelineItem item)
    {
        return new AgentRunTimelineItem
        {
            Id = item.Id,
            RunId = item.RunId,
            Timestamp = item.Timestamp,
            Stage = item.Stage,
            Status = item.Status,
            Message = item.Message,
            ReferenceId = item.ReferenceId
        };
    }
}
