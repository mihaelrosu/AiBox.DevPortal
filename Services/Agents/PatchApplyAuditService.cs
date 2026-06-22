using System.Text.Json;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Services.Agents;

public sealed class PatchApplyAuditService(IWebHostEnvironment environment)
{
    private const string FileName = "patch-apply-audit-log.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<PatchApplyAuditItem>> GetLatestAsync(int take = 10, CancellationToken cancellationToken = default)
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

    public async Task AddAsync(PatchApplyAuditItem item, CancellationToken cancellationToken = default)
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
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<PatchApplyAuditItem>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        return await JsonFileStore.LoadListAsync<PatchApplyAuditItem>(path, JsonOptions, cancellationToken, () => []);
    }

    private async Task SaveAsync(List<PatchApplyAuditItem> items, CancellationToken cancellationToken)
    {
        await JsonFileStore.SaveListAsync(GetHistoryPath(), items, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", FileName);
    }

    private static PatchApplyAuditItem Clone(PatchApplyAuditItem item)
    {
        return new PatchApplyAuditItem
        {
            Id = item.Id,
            Timestamp = item.Timestamp,
            Action = item.Action,
            PatchPreviewId = item.PatchPreviewId,
            ProfileId = item.ProfileId,
            PolicyId = item.PolicyId,
            ModelRouteId = item.ModelRouteId,
            SnapshotId = item.SnapshotId,
            ApprovedBy = item.ApprovedBy,
            ApprovedAt = item.ApprovedAt,
            Success = item.Success,
            Message = item.Message,
            ChangedFiles = [.. (item.ChangedFiles ?? [])],
            VerificationSucceeded = item.VerificationSucceeded,
            VerificationSummary = item.VerificationSummary,
            RestoreAttempted = item.RestoreAttempted,
            RestoreSucceeded = item.RestoreSucceeded,
            RestoreSummary = item.RestoreSummary
        };
    }
}
