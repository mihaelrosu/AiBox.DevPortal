namespace AiBox.DevPortal.Models;

public sealed class ScheduledAgentRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public string TaskName { get; set; } = string.Empty;
    public string UserRequest { get; set; } = string.Empty;
    public string ExecutionPolicyName { get; set; } = "Safe";
    public bool CommitAndSync { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastCompletedUtc { get; set; }
    public DateTime? LastFailedUtc { get; set; }
    public bool IsRunning { get; set; }
    public string? LastError { get; set; }
}
