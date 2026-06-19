namespace AiBox.DevPortal.Models;

public enum AgentOrchestrationTimelineEventType
{
    RunCreated,
    StepStarted,
    StepCompleted,
    StepFailed,
    SafetyReviewGenerated,
    ApprovalRequested,
    ApprovalGranted,
    ApprovalRejected,
    ApplySkipped,
    ApplyCompleted,
    GitSyncSkipped,
    GitSyncCompleted,
    GitSyncFailed
}

public enum AgentOrchestrationTimelineSeverity
{
    Info,
    Warning,
    Error
}

public sealed class AgentOrchestrationTimelineEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RunId { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public AgentOrchestrationTimelineEventType EventType { get; set; } = AgentOrchestrationTimelineEventType.RunCreated;
    public string StepName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public AgentOrchestrationTimelineSeverity Severity { get; set; } = AgentOrchestrationTimelineSeverity.Info;
    public string RelatedEntityId { get; set; } = string.Empty;
}
