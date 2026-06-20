namespace AiBox.DevPortal.Models;

public sealed class AgentOrchestrationCheckpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string CurrentStepName { get; set; } = string.Empty;
    public string NextStepName { get; set; } = string.Empty;
    public AgentOrchestrationStatus Status { get; set; } = AgentOrchestrationStatus.Paused;
    public string Message { get; set; } = string.Empty;
}
