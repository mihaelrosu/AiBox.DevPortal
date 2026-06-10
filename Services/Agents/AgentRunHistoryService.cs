using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentRunHistoryService(IWebHostEnvironment environment) : IAgentRunHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentRunRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentRunRecord> AddAsync(AgentRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var records = await LoadAsync(cancellationToken);
            var saved = Clone(record);
            saved.Id = string.IsNullOrWhiteSpace(saved.Id) ? Guid.NewGuid().ToString("N") : saved.Id;
            saved.Timestamp = saved.Timestamp == default ? DateTimeOffset.UtcNow : saved.Timestamp;

            var index = records.FindIndex(item => item.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                records.RemoveAt(index);
            }

            records.Insert(0, saved);
            await SaveAsync(records, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<AgentRunRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetPath();

        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentRunRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentRunRecord> records, CancellationToken cancellationToken)
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
    }

    private string GetPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "agent-runs.json");
    }

    private static AgentRunRecord Clone(AgentRunRecord record)
    {
        return new AgentRunRecord
        {
            Id = record.Id,
            Timestamp = record.Timestamp,
            ActionKey = record.ActionKey,
            ProfileMode = record.ProfileMode,
            Model = record.Model,
            UserRequest = record.UserRequest,
            PromptSent = record.PromptSent,
            ResultText = record.ResultText,
            Success = record.Success,
            ErrorMessage = record.ErrorMessage
        };
    }
}
