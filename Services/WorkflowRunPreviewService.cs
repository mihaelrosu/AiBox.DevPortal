using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class WorkflowRunPreviewService(
    IWorkflowRegistryService workflowRegistry,
    IAgentRegistryService agentRegistry,
    IProjectRegistryService projectRegistry) : IWorkflowRunPreviewService
{
    public async Task<WorkflowRunPreviewResult?> PreviewAsync(
        WorkflowRunPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.WorkflowId))
        {
            throw new ArgumentException("Workflow is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.GoalText))
        {
            throw new ArgumentException("Goal text is required.", nameof(request));
        }

        var workflow = await workflowRegistry.GetByIdAsync(request.WorkflowId, cancellationToken);

        if (workflow is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw new ArgumentException("Project is required.", nameof(request));
        }

        var agents = await agentRegistry.GetAllAsync(cancellationToken);
        var agentsById = agents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var project = await projectRegistry.GetByIdAsync(request.ProjectId, cancellationToken);

        if (project is null)
        {
            return null;
        }

        var goalText = request.GoalText.Trim();
        var steps = new List<WorkflowRunStepPreview>();
        var projectSnapshot = ToSnapshot(project);

        foreach (var step in workflow.Steps.OrderBy(step => step.Order))
        {
            if (string.IsNullOrWhiteSpace(step.AgentId) || !agentsById.TryGetValue(step.AgentId, out var agent))
            {
                throw new ArgumentException($"Workflow step '{step.Name}' does not reference a valid agent.", nameof(request));
            }

            steps.Add(new WorkflowRunStepPreview
            {
                StepId = step.Id,
                Order = step.Order,
                Enabled = step.Enabled,
                StepName = step.Name,
                AgentId = agent.Id,
                AgentName = agent.Name,
                Model = agent.Model,
                Instruction = step.Instruction,
                GeneratedTaskPrompt = WorkflowPromptBuilder.Build(projectSnapshot, goalText, agent.Name, agent.Model, step.Instruction),
                IncludePreviousResults = step.IncludePreviousResults,
                PreviousResultMode = step.PreviousResultMode,
                DependsOnStepIds = [.. (step.DependsOnStepIds ?? [])]
            });
        }

        return new WorkflowRunPreviewResult
        {
            WorkflowId = workflow.Id,
            WorkflowName = workflow.Name,
            GoalText = goalText,
            ProjectId = project.Id,
            ProjectSnapshot = projectSnapshot,
            Steps = steps
        };
    }

    private static ProjectSnapshot ToSnapshot(ProjectDefinition project)
    {
        return new ProjectSnapshot
        {
            Id = project.Id,
            Name = project.Name,
            Type = project.Type,
            Description = project.Description,
            LocalPath = project.LocalPath,
            GitRepository = project.GitRepository,
            DefaultBranch = project.DefaultBranch,
            BuildCommand = project.BuildCommand,
            RunCommand = project.RunCommand,
            TestCommand = project.TestCommand,
            DefaultExecutionPermissionProfileId = project.DefaultExecutionPermissionProfileId,
            Enabled = project.Enabled
        };
    }

}
