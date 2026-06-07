using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class LocalCoderHistoryService(IWebHostEnvironment environment) : ILocalCoderHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<LocalCoderTaskHistoryRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(record => record.UpdatedAt)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<LocalCoderTaskHistoryRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var record = (await LoadAsync(cancellationToken))
                .FirstOrDefault(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return record is null ? null : Clone(record);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<LocalCoderTaskHistoryRecord> SaveAsync(
        LocalCoderTaskHistoryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.Task);

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var records = await LoadAsync(cancellationToken);
            var saved = Clone(record);
            var now = DateTime.UtcNow;
            var index = records.FindIndex(item => item.Id.Equals(saved.Id, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                saved.Id = string.IsNullOrWhiteSpace(saved.Id) ? Guid.NewGuid().ToString("N") : saved.Id;
                saved.CreatedAt = saved.CreatedAt == default ? now : saved.CreatedAt;
                records.Add(saved);
            }
            else
            {
                saved.CreatedAt = records[index].CreatedAt;
                records[index] = saved;
            }

            saved.Task.HistoryId = saved.Id;
            saved.UpdatedAt = now;
            await SaveAllAsync(records, cancellationToken);
            return Clone(saved);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var records = await LoadAsync(cancellationToken);
            var removed = records.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAllAsync(records, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<LocalCoderTaskHistoryRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();

        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<LocalCoderTaskHistoryRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAllAsync(List<LocalCoderTaskHistoryRecord> records, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "local-coder-history.json");
    }

    private static LocalCoderTaskHistoryRecord Clone(LocalCoderTaskHistoryRecord record)
    {
        return new LocalCoderTaskHistoryRecord
        {
            Id = record.Id,
            Task = CloneTask(record.Task),
            PlanText = record.PlanText,
            DiffText = record.DiffText,
            BuildOutput = record.BuildOutput,
            ReviewText = record.ReviewText,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt
        };
    }

    private static LocalCoderTask CloneTask(LocalCoderTask task)
    {
        return new LocalCoderTask
        {
            HistoryId = task.HistoryId,
            Title = task.Title,
            RepositoryPath = task.RepositoryPath,
            Model = task.Model,
            Instructions = task.Instructions,
            AllowedPathsText = task.AllowedPathsText,
            ForbiddenPathsText = task.ForbiddenPathsText,
            SelectedFilePaths = [.. task.SelectedFilePaths ?? []],
            RequireApprovalBeforeApply = task.RequireApprovalBeforeApply,
            BuildCommand = task.BuildCommand,
            CreatedAt = task.CreatedAt,
            Status = task.Status
        };
    }
}
