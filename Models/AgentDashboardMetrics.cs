namespace AiBox.DevPortal.Models;

public sealed class AgentDashboardMetrics
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public int TotalAttempts { get; set; }
    public int SuccessfulApplies { get; set; }
    public int FailedApplies { get; set; }
    public int BuildSucceededAttempts { get; set; }
    public int BuildFailedAttempts { get; set; }
    public int RolledBackAttempts { get; set; }
    public int RollbackSucceededAttempts { get; set; }
    public int RollbackFailedAttempts { get; set; }
    public double SuccessRate { get; set; }
    public double BuildSuccessRate { get; set; }
    public double RollbackSuccessRate { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }
    public IReadOnlyList<TaskSliceApplyHistoryItem> RecentAttempts { get; set; } = [];
    public IReadOnlyList<string> TopFailureMessages { get; set; } = [];
}
