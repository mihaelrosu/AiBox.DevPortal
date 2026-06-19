using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Models;

public sealed class AgentOrchestrationStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepName { get; set; } = string.Empty;
    public AgentMode AgentRole { get; set; } = AgentMode.Planner;
    public AgentOrchestrationStatus Status { get; set; } = AgentOrchestrationStatus.Pending;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RelatedRunId { get; set; } = string.Empty;
}
