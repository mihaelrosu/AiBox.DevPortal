using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Models;

public sealed class AgentDashboardSummary
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public int TotalAgentRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
    public IReadOnlyList<AgentDashboardModelUsage> ModelUsage { get; set; } = [];
    public IReadOnlyList<AgentDashboardActionMetric> ActionMetrics { get; set; } = [];
    public int ApplyAttempts { get; set; }
    public int SuccessfulApplies { get; set; }
    public int BlockedApplies { get; set; }
    public int RollbackCount { get; set; }
    public int LowRiskApplyAttempts { get; set; }
    public int MediumRiskApplyAttempts { get; set; }
    public int HighRiskApplyAttempts { get; set; }
    public int CriticalRiskApplyAttempts { get; set; }
    public double OverallSuccessRate { get; set; }
    public double ApplySuccessRate { get; set; }
    public double? VerificationSuccessRate { get; set; }
    public IReadOnlyList<AgentRunRecord> RecentAgentRuns { get; set; } = [];
    public IReadOnlyList<TaskSliceApplyAuditEntry> RecentApplyAttempts { get; set; } = [];
    public IReadOnlyList<PatchRollbackEntry> RecentRollbacks { get; set; } = [];
}

public sealed class AgentDashboardModelUsage
{
    public string ModelName { get; set; } = string.Empty;
    public int TotalRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
}

public sealed class AgentDashboardActionMetric
{
    public string ActionKey { get; set; } = string.Empty;
    public int TotalRuns { get; set; }
    public int SuccessfulRuns { get; set; }
    public int FailedRuns { get; set; }
}
