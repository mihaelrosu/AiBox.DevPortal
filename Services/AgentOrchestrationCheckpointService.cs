using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentOrchestrationCheckpointService(IWebHostEnvironment environment)
{
    private const string CheckpointsFileName = "agent-orchestration-checkpoints.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentOrchestrationCheckpoint>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationCheckpoint?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var checkpoint = (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();

            return checkpoint is null ? null : Clone(checkpoint);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationCheckpoint> CreateAsync(AgentOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var saved = Clone(checkpoint);
        if (string.IsNullOrWhiteSpace(saved.Id))
        {
            saved.Id = Guid.NewGuid().ToString("N");
        }

        if (saved.CreatedAtUtc == default)
        {
            saved.CreatedAtUtc = DateTime.UtcNow;
        }

        saved.RunId = saved.RunId.Trim();
        saved.CurrentStepName = saved.CurrentStepName.Trim();
        saved.NextStepName = saved.NextStepName.Trim();
        saved.Message = saved.Message.Trim();

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            items.Add(Clone(saved));
            await SaveAsync(items, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }

        return Clone(saved);
    }

    private async Task<List<AgentOrchestrationCheckpoint>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetCheckpointsPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentOrchestrationCheckpoint>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentOrchestrationCheckpoint> items, CancellationToken cancellationToken)
    {
        var path = GetCheckpointsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetCheckpointsPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", CheckpointsFileName);
    }

    private static AgentOrchestrationCheckpoint Clone(AgentOrchestrationCheckpoint checkpoint)
    {
        return new AgentOrchestrationCheckpoint
        {
            Id = checkpoint.Id,
            RunId = checkpoint.RunId,
            CreatedAtUtc = checkpoint.CreatedAtUtc,
            CurrentStepName = checkpoint.CurrentStepName,
            NextStepName = checkpoint.NextStepName,
            Status = checkpoint.Status,
            Message = checkpoint.Message
        };
    }
}
