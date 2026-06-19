using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentDashboardMetricsService(TaskSliceApplyHistoryService taskSliceApplyHistoryService)
{
    public async Task<AgentDashboardMetrics> GetMetricsAsync(CancellationToken cancellationToken = default)
    {
        var history = await taskSliceApplyHistoryService.GetHistoryAsync(int.MaxValue, cancellationToken);
        var ordered = history.OrderByDescending(item => item.AppliedAtUtc).ToArray();
        var total = ordered.Length;
        var buildSucceededCount = ordered.Count(item => item.BuildSucceeded);
        var rolledBackCount = ordered.Count(item => item.RolledBack);
        var rollbackSucceededCount = ordered.Count(item => item.RolledBack && item.RollbackSucceeded);
        var rollbackFailedCount = ordered.Count(item => item.RolledBack && !item.RollbackSucceeded);

        return new AgentDashboardMetrics
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalAttempts = total,
            SuccessfulApplies = buildSucceededCount,
            FailedApplies = total - buildSucceededCount,
            BuildSucceededAttempts = buildSucceededCount,
            BuildFailedAttempts = total - buildSucceededCount,
            RolledBackAttempts = rolledBackCount,
            RollbackSucceededAttempts = rollbackSucceededCount,
            RollbackFailedAttempts = rollbackFailedCount,
            SuccessRate = ToRate(buildSucceededCount, total),
            BuildSuccessRate = ToRate(buildSucceededCount, total),
            RollbackSuccessRate = ToRate(rollbackSucceededCount, rolledBackCount),
            LastAttemptAtUtc = ordered.FirstOrDefault()?.AppliedAtUtc,
            LastSuccessAtUtc = ordered.FirstOrDefault(item => item.BuildSucceeded)?.AppliedAtUtc,
            LastFailureAtUtc = ordered.FirstOrDefault(item => !item.BuildSucceeded)?.AppliedAtUtc,
            RecentAttempts = ordered.Take(10).Select(Clone).ToArray(),
            TopFailureMessages = ordered
                .Where(item => !item.BuildSucceeded || (item.RolledBack && !item.RollbackSucceeded))
                .Select(item => string.IsNullOrWhiteSpace(item.ResultMessage) ? "Unknown failure" : item.ResultMessage.Trim())
                .GroupBy(message => message, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .Select(group => $"{group.Key} ({group.Count()})")
                .ToArray()
        };
    }

    private static double ToRate(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }

        return Math.Round(numerator * 100d / denominator, 1, MidpointRounding.AwayFromZero);
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
