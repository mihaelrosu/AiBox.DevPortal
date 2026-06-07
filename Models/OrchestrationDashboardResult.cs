namespace AiBox.DevPortal.Models;

public sealed class OrchestrationDashboardResult
{
    public IReadOnlyList<OrchestrationRunSummary> ActiveRuns { get; set; } = [];
    public IReadOnlyList<OrchestrationRunSummary> RecentRuns { get; set; } = [];
    public IReadOnlyList<OrchestrationAgentStatus> Agents { get; set; } = [];
    public IReadOnlyList<ProjectDefinition> Projects { get; set; } = [];
    public IReadOnlyList<OrchestrationVerificationSummary> Verifications { get; set; } = [];
    public DateTimeOffset GeneratedAt { get; set; }
}

public sealed class OrchestrationRunSummary
{
    public string RunId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public WorkflowRunStatus Status { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public int CompletedSteps { get; set; }
    public int TotalSteps { get; set; }
    public double ProgressPercent { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Started { get; set; }
    public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.NotChecked;
}

public sealed class OrchestrationAgentStatus
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PermissionProfile { get; set; } = string.Empty;
    public bool ExecutionAllowed { get; set; }
}

public sealed class OrchestrationVerificationSummary
{
    public string RunId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public VerificationStatus Status { get; set; } = VerificationStatus.NotChecked;
    public string RecommendedNextAction { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
