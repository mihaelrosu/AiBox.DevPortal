using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApplyAuditService(IWebHostEnvironment environment)
{
    private const string HistoryFileName = "task-slice-apply-audit.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<TaskSliceApplyAuditEntry>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
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

    public async Task AddAsync(TaskSliceApplyAuditEntry item, CancellationToken cancellationToken = default)
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
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<TaskSliceApplyAuditEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TaskSliceApplyAuditEntry>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<TaskSliceApplyAuditEntry> items, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", HistoryFileName);
    }

    private static TaskSliceApplyAuditEntry Clone(TaskSliceApplyAuditEntry item)
    {
        return new TaskSliceApplyAuditEntry
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            PlanId = item.PlanId,
            SliceId = item.SliceId,
            SliceTitle = item.SliceTitle,
            RiskLevel = item.RiskLevel,
            RiskScore = item.RiskScore,
            ApprovedHighRiskApply = item.ApprovedHighRiskApply,
            Success = item.Success,
            Blocked = item.Blocked,
            Message = item.Message,
            ChangedFiles = [.. item.ChangedFiles ?? []]
        };
    }
}
