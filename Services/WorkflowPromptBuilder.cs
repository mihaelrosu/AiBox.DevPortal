using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

internal static class WorkflowPromptBuilder
{
    public static string Build(
        WorkflowRunRecord run,
        WorkflowRunStepRecord step)
    {
        return Build(
            step.AgentName,
            step.Model,
            run.ProjectName ?? string.Empty,
            run.GoalText,
            step.Instruction,
            SelectPreviousResults(run.Steps, step));
    }

    public static string Build(
        string agentName,
        string model,
        string projectName,
        string goalText,
        string instruction,
        IReadOnlyCollection<WorkflowRunStepRecord>? previousResults = null)
    {
        var prompt = new StringBuilder($"""
            [Agent Role]
            You are: {agentName}
            Model: {model}

            [Project]
            {projectName}

            [Goal]
            {goalText}

            [Step Instruction]
            {instruction}
            """);

        if (previousResults is { Count: > 0 })
        {
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.AppendLine("[Previous Step Results]");

            foreach (var previousStep in previousResults)
            {
                prompt.AppendLine($"Step: {previousStep.StepName}");
                prompt.AppendLine($"Agent: {previousStep.AgentName}");
                prompt.AppendLine("Result:");
                prompt.AppendLine(previousStep.ResultText.Trim());
                prompt.AppendLine();
            }
        }

        prompt.Append("""

            [Expected Output]
            Return a clear result for this workflow step.
            Do not execute commands.
            Do not modify files.
            """);

        return prompt.ToString();
    }

    private static IReadOnlyCollection<WorkflowRunStepRecord> SelectPreviousResults(
        IEnumerable<WorkflowRunStepRecord> steps,
        WorkflowRunStepRecord currentStep)
    {
        if (!currentStep.IncludePreviousResults || currentStep.PreviousResultMode == PreviousResultMode.None)
        {
            return [];
        }

        var completedPreviousSteps = steps
            .Where(step =>
                step.Order < currentStep.Order
                && step.Status == WorkflowRunStepStatus.Completed
                && !string.IsNullOrWhiteSpace(step.ResultText))
            .OrderBy(step => step.Order);

        return currentStep.PreviousResultMode switch
        {
            PreviousResultMode.AllPreviousSteps => completedPreviousSteps.ToArray(),
            PreviousResultMode.LastCompletedStep => completedPreviousSteps.TakeLast(1).ToArray(),
            PreviousResultMode.SelectedSteps => completedPreviousSteps
                .Where(step => (currentStep.DependsOnStepIds ?? []).Contains(step.StepId, StringComparer.OrdinalIgnoreCase))
                .ToArray(),
            _ => []
        };
    }
}
