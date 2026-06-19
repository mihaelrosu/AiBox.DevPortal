using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApplyHistoryService(IWebHostEnvironment environment)
{
    private const string HistoryFileName = "task-slice-apply-history.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<TaskSliceApplyHistoryItem>> GetHistoryAsync(int take = 50, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.AppliedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task AddAsync(TaskSliceApplyHistoryItem item, CancellationToken cancellationToken = default)
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

            if (saved.AppliedAtUtc == default)
            {
                saved.AppliedAtUtc = DateTime.UtcNow;
            }

            items.Add(saved);
            await SaveAsync(items, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var path = GetHistoryPath();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<TaskSliceApplyHistoryItem>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TaskSliceApplyHistoryItem>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<TaskSliceApplyHistoryItem> items, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", HistoryFileName);
    }

    private static TaskSliceApplyHistoryItem Clone(TaskSliceApplyHistoryItem item)
    {
        return new TaskSliceApplyHistoryItem
        {
            Id = item.Id,
            PlanId = item.PlanId,
            SliceId = item.SliceId,
            SliceTitle = item.SliceTitle,
            PatchPackageId = item.PatchPackageId,
            AppliedAtUtc = item.AppliedAtUtc,
            BackupFolderPath = item.BackupFolderPath,
            BackedUpFilesCount = item.BackedUpFilesCount,
            AppliedFilesCount = item.AppliedFilesCount,
            BuildSucceeded = item.BuildSucceeded,
            RolledBack = item.RolledBack,
            RollbackSucceeded = item.RollbackSucceeded,
            ResultMessage = item.ResultMessage
        };
    }
}
