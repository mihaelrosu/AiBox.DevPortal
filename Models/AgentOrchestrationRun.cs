namespace AiBox.DevPortal.Models;

public sealed class AgentOrchestrationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TaskName { get; set; } = string.Empty;
    public string UserRequest { get; set; } = string.Empty;
    public AgentOrchestrationStatus Status { get; set; } = AgentOrchestrationStatus.Pending;
    public List<AgentOrchestrationStep> Steps { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public bool CommitAndSync { get; set; }
    public bool ApproveHighRiskApply { get; set; }
    public string ExecutionPolicyName { get; set; } = "Safe";
    public string HumanApprovalRequestId { get; set; } = string.Empty;
    public bool HumanApprovalPending { get; set; }
    public DateTime? PausedAtUtc { get; set; }
    public string PausedReason { get; set; } = string.Empty;
    public int NextStepIndex { get; set; }
    public string CheckpointPlanId { get; set; } = string.Empty;
    public string CheckpointSliceId { get; set; } = string.Empty;
    public string CheckpointSliceTitle { get; set; } = string.Empty;
    public string CheckpointPatchPackageId { get; set; } = string.Empty;
    public RiskLevel CheckpointRiskLevel { get; set; } = RiskLevel.Low;
    public bool ApplySucceeded { get; set; }
    public string ApplyMessage { get; set; } = string.Empty;
    public string ApplyRiskGateMessage { get; set; } = string.Empty;
    public List<string> AppliedSliceIds { get; set; } = [];
    public List<string> AppliedFiles { get; set; } = [];
    public List<string> ApplyAuditIds { get; set; } = [];
    public bool CommitAttempted { get; set; }
    public bool CommitSucceeded { get; set; }
    public string CommitHash { get; set; } = string.Empty;
    public bool PushAttempted { get; set; }
    public bool PushSucceeded { get; set; }
    public string GitMessage { get; set; } = string.Empty;
    public string SafetyReportId { get; set; } = string.Empty;
    public DateTime? SafetyReportCreatedAtUtc { get; set; }
    public RiskLevel SafetyHighestRiskLevel { get; set; } = RiskLevel.Low;
    public int SafetyTotalChangedFiles { get; set; }
    public bool SafetyRequiresManualApproval { get; set; }
    public bool SafetyBlocksAutoApply { get; set; }
    public List<string> SafetyReasons { get; set; } = [];
    public string SafetySummary { get; set; } = string.Empty;
}
