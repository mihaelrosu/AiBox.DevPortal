using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskWorkflowPlanValidationService
{
    public IReadOnlyList<string> Validate(TaskWorkflowPlan plan)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Goal))
        {
            errors.Add("Workflow goal is required.");
        }

        if (plan.Slices.Count == 0)
        {
            errors.Add("Workflow must contain at least one slice.");
        }

        var validSliceIds = plan.Slices
            .Select(x => x.SliceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var slice in plan.Slices)
        {
            if (string.IsNullOrWhiteSpace(slice.SliceId))
            {
                errors.Add("Slice id is required.");
            }

            if (string.IsNullOrWhiteSpace(slice.Title))
            {
                errors.Add("Slice title is required.");
            }

            if (string.IsNullOrWhiteSpace(slice.Instruction))
            {
                errors.Add("Slice instruction is required.");
            }

            if (string.IsNullOrWhiteSpace(slice.RiskLevel))
            {
                errors.Add("Slice risk level is required.");
            }

            if (slice.TargetFiles.Count == 0)
            {
                errors.Add("Slice target files are required.");
            }

            if (slice.TargetFiles.Count > 5)
            {
                errors.Add("Slice modifies too many files.");
            }

            foreach (var dependencyId in slice.DependsOnSliceIds)
            {
                if (!validSliceIds.Contains(dependencyId))
                {
                    errors.Add("Slice depends on unknown slice id.");
                }
            }
        }

        return errors;
    }
}
