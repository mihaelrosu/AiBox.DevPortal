using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApprovalService(IWebHostEnvironment environment)
{
    private const string HistoryFileName = "task-slice-approvals.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<TaskSliceApproval>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => GetOrderTimestamp(item))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<TaskSliceApproval> GetOrCreateAsync(TaskPlanSlice slice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var approvals = await LoadAsync(cancellationToken);
            var approval = approvals.FirstOrDefault(item => item.SliceId.Equals(slice.Id, StringComparison.OrdinalIgnoreCase));
            var needsSave = false;
            if (approval is null)
            {
                approval = new TaskSliceApproval
                {
                    PlanId = slice.PlanId,
                    SliceId = slice.Id,
                    SliceTitle = slice.Title
                };

                approval = ApplyAutomaticApprovalRules(slice, approval);
                approvals.Add(approval);
                needsSave = true;
            }
            else
            {
                if (!approval.PlanId.Equals(slice.PlanId, StringComparison.Ordinal))
                {
                    approval.PlanId = slice.PlanId;
                    needsSave = true;
                }

                if (!approval.SliceTitle.Equals(slice.Title, StringComparison.Ordinal))
                {
                    approval.SliceTitle = slice.Title;
                    needsSave = true;
                }
            }

            if (needsSave)
            {
                await SaveAsync(approvals, cancellationToken);
            }

            return Clone(approval);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<TaskSliceApproval> ApproveAsync(
        TaskPlanSlice slice,
        string? approvedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var approvals = await LoadAsync(cancellationToken);
            var approval = approvals.FirstOrDefault(item => item.SliceId.Equals(slice.Id, StringComparison.OrdinalIgnoreCase));
            approval ??= new TaskSliceApproval
            {
                Id = Guid.NewGuid().ToString("N"),
                PlanId = slice.PlanId,
                SliceId = slice.Id,
                SliceTitle = slice.Title
            };

            approval.PlanId = slice.PlanId;
            approval.SliceTitle = slice.Title;
            approval.Status = TaskSliceApprovalStatus.Approved;
            approval.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? Environment.UserName : approvedBy.Trim();
            approval.ApprovedAtUtc = DateTime.UtcNow;
            approval.RejectedBy = null;
            approval.RejectedAtUtc = null;

            Upsert(approvals, approval);
            await SaveAsync(approvals, cancellationToken);
            return Clone(approval);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<TaskSliceApproval> RejectAsync(
        TaskPlanSlice slice,
        string? rejectedBy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var approvals = await LoadAsync(cancellationToken);
            var approval = approvals.FirstOrDefault(item => item.SliceId.Equals(slice.Id, StringComparison.OrdinalIgnoreCase));
            approval ??= new TaskSliceApproval
            {
                Id = Guid.NewGuid().ToString("N"),
                PlanId = slice.PlanId,
                SliceId = slice.Id,
                SliceTitle = slice.Title
            };

            approval.PlanId = slice.PlanId;
            approval.SliceTitle = slice.Title;
            approval.Status = TaskSliceApprovalStatus.Rejected;
            approval.RejectedBy = string.IsNullOrWhiteSpace(rejectedBy) ? Environment.UserName : rejectedBy.Trim();
            approval.RejectedAtUtc = DateTime.UtcNow;
            approval.ApprovedBy = null;
            approval.ApprovedAtUtc = null;

            Upsert(approvals, approval);
            await SaveAsync(approvals, cancellationToken);
            return Clone(approval);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<TaskSliceApprovalStatus> GetStatusAsync(TaskPlanSlice slice, CancellationToken cancellationToken = default)
    {
        var approval = await GetOrCreateAsync(slice, cancellationToken);
        return approval.Status;
    }

    public async Task<bool> CanApplyAsync(TaskPlanSlice slice, CancellationToken cancellationToken = default)
    {
        return await GetStatusAsync(slice, cancellationToken) == TaskSliceApprovalStatus.Approved;
    }

    private static TaskSliceApproval ApplyAutomaticApprovalRules(TaskPlanSlice slice, TaskSliceApproval approval)
    {
        // Automatic approval rules can be introduced here later without changing callers.
        return approval;
    }

    private async Task<List<TaskSliceApproval>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TaskSliceApproval>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<TaskSliceApproval> approvals, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, approvals, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", HistoryFileName);
    }

    private static void Upsert(List<TaskSliceApproval> approvals, TaskSliceApproval approval)
    {
        var index = approvals.FindIndex(item => item.SliceId.Equals(approval.SliceId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            approvals[index] = approval;
        }
        else
        {
            approvals.Add(approval);
        }
    }

    private static TaskSliceApproval Clone(TaskSliceApproval item)
    {
        return new TaskSliceApproval
        {
            Id = item.Id,
            PlanId = item.PlanId,
            SliceId = item.SliceId,
            SliceTitle = item.SliceTitle,
            Status = item.Status,
            ApprovedBy = item.ApprovedBy,
            ApprovedAtUtc = item.ApprovedAtUtc,
            RejectedBy = item.RejectedBy,
            RejectedAtUtc = item.RejectedAtUtc
        };
    }

    private static DateTime GetOrderTimestamp(TaskSliceApproval item)
    {
        return item.ApprovedAtUtc ?? item.RejectedAtUtc ?? DateTime.MinValue;
    }
}
