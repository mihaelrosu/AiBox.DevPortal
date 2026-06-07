using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class OrchestrationDashboardService(
    IWorkflowRunHistoryService workflowRunHistoryService,
    IAgentRegistryService agentRegistryService,
    IExecutionPermissionProfileService permissionProfileService,
    IProjectRegistryService projectRegistryService,
    IVerificationService verificationService) : IOrchestrationDashboardService
{
    public async Task<OrchestrationDashboardResult> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var runs = await workflowRunHistoryService.GetAllAsync(cancellationToken);
        var agents = await agentRegistryService.GetAllAsync(cancellationToken);
        var profiles = await permissionProfileService.GetAllAsync(cancellationToken);
        var projects = await projectRegistryService.GetAllAsync(cancellationToken);
        var profilesById = profiles.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        var recentRuns = runs.OrderByDescending(item => item.Created).Take(20).ToArray();
        var latestVerifications = new Dictionary<string, VerificationResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in runs)
        {
            var verification = await verificationService.GetLatestForRunAsync(run.Id, cancellationToken);
            if (verification is not null)
            {
                latestVerifications[run.Id] = verification;
            }
        }

        return new OrchestrationDashboardResult
        {
            ActiveRuns = runs
                .Where(item => item.Status is WorkflowRunStatus.Planned or WorkflowRunStatus.Running or WorkflowRunStatus.Paused)
                .OrderByDescending(item => item.Created)
                .Select(run => ToSummary(run, latestVerifications.GetValueOrDefault(run.Id)))
                .ToArray(),
            RecentRuns = recentRuns.Select(run => ToSummary(run, latestVerifications.GetValueOrDefault(run.Id))).ToArray(),
            Agents = agents
                .Where(agent => agent.Enabled)
                .Select(agent => ToAgentStatus(agent, profilesById.GetValueOrDefault(agent.ExecutionPermissionProfileId)))
                .ToArray(),
            Projects = projects.Where(project => project.Enabled).ToArray(),
            Verifications = latestVerifications
                .Select(pair =>
                {
                    var run = runs.First(item => item.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                    return new OrchestrationVerificationSummary
                    {
                        RunId = pair.Key,
                        WorkflowName = run.WorkflowName,
                        Status = pair.Value.Status,
                        RecommendedNextAction = pair.Value.RecommendedNextAction,
                        CreatedAt = pair.Value.CreatedAt
                    };
                })
                .OrderByDescending(item => item.CreatedAt)
                .ToArray(),
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private static OrchestrationRunSummary ToSummary(WorkflowRunRecord run, VerificationResult? verification)
    {
        var enabled = run.Steps.Where(step => step.Enabled).OrderBy(step => step.Order).ToArray();
        var completed = enabled.Count(step => step.Status is WorkflowRunStepStatus.Completed or WorkflowRunStepStatus.Skipped);
        var current = enabled.FirstOrDefault(step => step.Status == WorkflowRunStepStatus.Running)
            ?? enabled.FirstOrDefault(step => step.Status == WorkflowRunStepStatus.Planned)
            ?? enabled.LastOrDefault();
        return new OrchestrationRunSummary
        {
            RunId = run.Id,
            WorkflowName = run.WorkflowName,
            ProjectName = run.ProjectSnapshot?.Name ?? "Not specified",
            Status = run.Status,
            CurrentStep = current?.StepName ?? "No steps",
            CompletedSteps = completed,
            TotalSteps = enabled.Length,
            ProgressPercent = enabled.Length == 0 ? 0 : completed * 100d / enabled.Length,
            Created = run.Created,
            Started = enabled.Where(step => step.StartedAt.HasValue).Select(step => step.StartedAt!.Value).DefaultIfEmpty(run.Created).Min(),
            VerificationStatus = verification?.Status ?? VerificationStatus.NotChecked
        };
    }

    private static OrchestrationAgentStatus ToAgentStatus(Models.Agents.AgentDefinition agent, ExecutionPermissionProfile? profile)
    {
        return new OrchestrationAgentStatus
        {
            AgentId = agent.Id,
            Name = agent.Name,
            Model = agent.Model,
            PermissionProfile = profile?.Name ?? "Not configured",
            ExecutionAllowed = profile?.Enabled == true && (profile.AllowRunShell || profile.AllowRunDotNet || profile.AllowRunDocker || profile.AllowRunGit || profile.AllowRunPython)
        };
    }
}
