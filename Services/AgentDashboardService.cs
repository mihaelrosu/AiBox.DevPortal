using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentDashboardService(
    IAgentRunHistoryService agentRunHistoryService,
    TaskSliceApplyAuditService taskSliceApplyAuditService,
    IPatchRollbackService patchRollbackService)
{
    public async Task<AgentDashboardSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var agentRuns = (await agentRunHistoryService.GetAllAsync(cancellationToken) ?? [])
            .OrderByDescending(item => item.Timestamp)
            .ToArray();

        var applyAudits = (await taskSliceApplyAuditService.GetLatestAsync(int.MaxValue, cancellationToken) ?? [])
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();

        var rollbacks = (await patchRollbackService.GetLatestAsync(int.MaxValue, cancellationToken) ?? [])
            .OrderByDescending(item => item.TimestampUtc)
            .ToArray();

        var modelUsage = agentRuns
            .GroupBy(item => NormalizeModelName(item.Model), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AgentDashboardModelUsage
            {
                ModelName = group.Key,
                TotalRuns = group.Count(),
                SuccessfulRuns = group.Count(item => item.Success),
                FailedRuns = group.Count(item => !item.Success)
            })
            .OrderByDescending(item => item.TotalRuns)
            .ThenBy(item => item.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actionMetrics = BuildActionMetrics(agentRuns, applyAudits, rollbacks);

        var totalRuns = agentRuns.Length;
        var successfulRuns = agentRuns.Count(item => item.Success);
        var verificationRuns = agentRuns.Where(item => IsVerificationAction(item.ActionKey)).ToArray();
        var verificationSuccessRate = verificationRuns.Length == 0
            ? (double?)null
            : ToRate(verificationRuns.Count(item => item.Success), verificationRuns.Length);

        return new AgentDashboardSummary
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalAgentRuns = totalRuns,
            SuccessfulRuns = successfulRuns,
            FailedRuns = totalRuns - successfulRuns,
            OverallSuccessRate = ToRate(successfulRuns, totalRuns),
            ModelUsage = modelUsage,
            ActionMetrics = actionMetrics,
            ApplyAttempts = applyAudits.Length,
            SuccessfulApplies = applyAudits.Count(item => item.Success),
            BlockedApplies = applyAudits.Count(item => item.Blocked),
            ApplySuccessRate = ToRate(applyAudits.Count(item => item.Success), applyAudits.Length),
            RollbackCount = rollbacks.Length,
            LowRiskApplyAttempts = applyAudits.Count(item => item.RiskLevel == RiskLevel.Low),
            MediumRiskApplyAttempts = applyAudits.Count(item => item.RiskLevel == RiskLevel.Medium),
            HighRiskApplyAttempts = applyAudits.Count(item => item.RiskLevel == RiskLevel.High),
            CriticalRiskApplyAttempts = applyAudits.Count(item => item.RiskLevel == RiskLevel.Critical),
            VerificationSuccessRate = verificationSuccessRate,
            RecentAgentRuns = agentRuns.Take(5).Select(Clone).ToArray(),
            RecentApplyAttempts = applyAudits.Take(5).Select(Clone).ToArray(),
            RecentRollbacks = rollbacks.Take(5).Select(Clone).ToArray()
        };
    }

    private static IReadOnlyList<AgentDashboardActionMetric> BuildActionMetrics(
        IReadOnlyList<AgentRunRecord> agentRuns,
        IReadOnlyList<TaskSliceApplyAuditEntry> applyAudits,
        IReadOnlyList<PatchRollbackEntry> rollbacks)
    {
        var metrics = new List<AgentDashboardActionMetric>
        {
            BuildAgentActionMetric(agentRuns, AgentActionProfiles.CreatePlanActionKey),
            BuildAgentActionMetric(agentRuns, AgentActionProfiles.GeneratePatchPreviewActionKey),
            BuildAgentActionMetric(agentRuns, AgentActionProfiles.VerifyProjectActionKey),
            BuildAgentActionMetric(agentRuns, "review"),
            BuildApplyActionMetric(applyAudits),
            BuildRollbackActionMetric(rollbacks)
        };

        return metrics;
    }

    private static AgentDashboardActionMetric BuildAgentActionMetric(
        IReadOnlyList<AgentRunRecord> agentRuns,
        string actionKey)
    {
        var runs = agentRuns.Where(item => string.Equals(NormalizeActionKey(item.ActionKey), actionKey, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new AgentDashboardActionMetric
        {
            ActionKey = actionKey,
            TotalRuns = runs.Length,
            SuccessfulRuns = runs.Count(item => item.Success),
            FailedRuns = runs.Count(item => !item.Success)
        };
    }

    private static AgentDashboardActionMetric BuildApplyActionMetric(IReadOnlyList<TaskSliceApplyAuditEntry> applyAudits)
    {
        return new AgentDashboardActionMetric
        {
            ActionKey = "apply",
            TotalRuns = applyAudits.Count,
            SuccessfulRuns = applyAudits.Count(item => item.Success),
            FailedRuns = applyAudits.Count(item => !item.Success)
        };
    }

    private static AgentDashboardActionMetric BuildRollbackActionMetric(IReadOnlyList<PatchRollbackEntry> rollbacks)
    {
        return new AgentDashboardActionMetric
        {
            ActionKey = "rollback",
            TotalRuns = rollbacks.Count,
            SuccessfulRuns = rollbacks.Count(item => item.Restored),
            FailedRuns = rollbacks.Count(item => !item.Restored)
        };
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
            ErrorMessage = record.ErrorMessage,
            PatchVerificationResult = record.PatchVerificationResult
        };
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

    private static string NormalizeModelName(string? model)
    {
        return string.IsNullOrWhiteSpace(model) ? "Unknown" : model.Trim();
    }

    private static string NormalizeActionKey(string? actionKey)
    {
        return string.IsNullOrWhiteSpace(actionKey) ? string.Empty : actionKey.Trim();
    }

    private static bool IsVerificationAction(string? actionKey)
    {
        return string.Equals(NormalizeActionKey(actionKey), AgentActionProfiles.VerifyProjectActionKey, StringComparison.OrdinalIgnoreCase);
    }

    private static PatchRollbackEntry Clone(PatchRollbackEntry item)
    {
        return new PatchRollbackEntry
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            PlanId = item.PlanId,
            SliceId = item.SliceId,
            BackupId = item.BackupId,
            ChangedFiles = [.. item.ChangedFiles ?? []],
            Restored = item.Restored,
            RestoreTimestampUtc = item.RestoreTimestampUtc
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
}
