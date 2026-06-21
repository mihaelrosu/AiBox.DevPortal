namespace AiBox.DevPortal.Models
{
    public sealed class ScheduledAgentExecution
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string ScheduledRunId { get; set; } = string.Empty;
        public string ScheduleName { get; set; } = string.Empty;
        public string TaskName { get; set; } = string.Empty;
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public long DurationMs { get; set; }
    }
}