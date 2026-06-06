using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class WorkflowRunPreviewService(
    IWorkflowRegistryService workflowRegistry,
    IAgentRegistryService agentRegistry) : IWorkflowRunPreviewService
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

        var agents = await agentRegistry.GetAllAsync(cancellationToken);
        var agentsById = agents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var projectName = request.ProjectName?.Trim() ?? string.Empty;
        var goalText = request.GoalText.Trim();
        var steps = new List<WorkflowRunStepPreview>();

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
                GeneratedTaskPrompt = WorkflowPromptBuilder.Build(agent.Name, agent.Model, projectName, goalText, step.Instruction),
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
            ProjectName = string.IsNullOrWhiteSpace(projectName) ? null : projectName,
            Steps = steps
        };
    }

}
