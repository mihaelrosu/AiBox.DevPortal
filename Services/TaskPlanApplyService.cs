using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskPlanApplyService(
    TaskSliceApplyService taskSliceApplyService,
    TaskSliceApprovalService taskSliceApprovalService,
    TaskPlanDependencyGraphService taskPlanDependencyGraphService)
{
    public async Task<TaskPlanApplyResult> ApplyAsync(
        TaskPlan plan,
        string projectPath,
        Func<TaskPlanApplyResult, Task>? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            return BuildFailureResult(plan, "Project path is required and must exist.");
        }

        var executionGraph = taskPlanDependencyGraphService.BuildExecutionGraph(plan.Slices);
        if (executionGraph.CyclesDetected.Count > 0 || executionGraph.ValidationErrors.Count > 0)
        {
            return BuildGraphFailureResult(plan, executionGraph);
        }

        var approvedSliceIds = await GetApprovedSliceIdsAsync(plan, cancellationToken);
        if (approvedSliceIds.Count == 0)
        {
            return BuildFailureResult(plan, "At least one approved slice is required before applying a plan.");
        }

        var planSlicesById = plan.Slices.ToDictionary(slice => slice.Id, StringComparer.OrdinalIgnoreCase);
        var orderedApprovedSlices = executionGraph.OrderedSliceIds
            .Where(approvedSliceIds.Contains)
            .Select(sliceId => planSlicesById[sliceId])
            .ToList();

        var result = new TaskPlanApplyResult
        {
            PlanId = plan.Id,
            PlanTitle = plan.OriginalRequest,
            TotalSlices = approvedSliceIds.Count,
            AppliedSlices = 0,
            Success = false,
            RollbackPerformed = false,
            StartedAtUtc = DateTime.UtcNow,
            StatusMessage = $"Plan apply started with {approvedSliceIds.Count} approved slice(s).",
            OrderedSliceIds = [.. orderedApprovedSlices.Select(slice => slice.Id)]
        };

        result = await ReportAsync(result, progressReporter, cancellationToken);
        AppendAudit(result, $"Plan apply started at {result.StartedAtUtc:O} for '{plan.OriginalRequest}'.");
        result = await ReportAsync(result, progressReporter, cancellationToken);

        var processedApprovedSlices = 0;

        foreach (var slice in orderedApprovedSlices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            processedApprovedSlices++;
            result.CurrentSliceId = slice.Id;
            result.CurrentSliceTitle = slice.Title;
            result.CurrentSliceIndex = processedApprovedSlices;
            result.StatusMessage = $"Applying slice {processedApprovedSlices}/{approvedSliceIds.Count}: {slice.Title}";
            AppendAudit(result, result.StatusMessage);
            result = await ReportAsync(result, progressReporter, cancellationToken);

            var sliceResult = await taskSliceApplyService.ApplyAsync(projectPath, slice, cancellationToken);
            var sliceResults = result.SliceResults.ToList();
            sliceResults.Add(sliceResult);
            result.SliceResults = sliceResults;

            if (sliceResult.Success)
            {
                result.AppliedSlices++;
                result.StatusMessage = $"Applied slice {processedApprovedSlices}/{approvedSliceIds.Count}: {slice.Title}";
                AppendAudit(result, result.StatusMessage);
                result = await ReportAsync(result, progressReporter, cancellationToken);
                continue;
            }

            result.FailedSliceId = slice.Id;
            result.RollbackPerformed = sliceResult.RolledBack;
            result.StatusMessage = sliceResult.RolledBack
                ? $"Slice '{slice.Title}' failed and was rolled back."
                : $"Slice '{slice.Title}' failed.";
            AppendAudit(result, $"Slice result: {sliceResult.Message}");
            if (!string.IsNullOrWhiteSpace(sliceResult.RollbackMessage))
            {
                AppendAudit(result, $"Rollback result: {sliceResult.RollbackMessage}");
            }

            result.FinishedAtUtc = DateTime.UtcNow;
            result.Success = false;
            result.Summary = BuildFailureSummary(result, slice);
            AppendAudit(result, $"Plan apply finished at {result.FinishedAtUtc:O}.");
            result = await ReportAsync(result, progressReporter, cancellationToken);
            return Clone(result);
        }

        result.Success = result.AppliedSlices > 0 && result.FailedSliceId is null;
        result.FinishedAtUtc = DateTime.UtcNow;
        result.StatusMessage = result.Success
            ? $"Applied {result.AppliedSlices}/{result.TotalSlices} approved slice(s)."
            : "Plan apply completed without applying any slices.";
        result.Summary = result.Success
            ? $"Applied {result.AppliedSlices} approved slice(s) successfully."
            : "No approved slices were applied.";
        AppendAudit(result, $"Plan apply finished at {result.FinishedAtUtc:O}.");
        result = await ReportAsync(result, progressReporter, cancellationToken);
        return Clone(result);
    }

    private async Task<IReadOnlySet<string>> GetApprovedSliceIdsAsync(
        TaskPlan plan,
        CancellationToken cancellationToken)
    {
        var approvals = await taskSliceApprovalService.GetAllAsync(cancellationToken);
        var approvalsBySliceId = approvals.ToDictionary(item => item.SliceId, StringComparer.OrdinalIgnoreCase);

        return plan.Slices
            .Where(slice =>
                approvalsBySliceId.TryGetValue(slice.Id, out var approval) &&
                approval.Status == TaskSliceApprovalStatus.Approved)
            .Select(slice => slice.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static TaskPlanApplyResult BuildFailureResult(TaskPlan plan, string message)
    {
        return new TaskPlanApplyResult
        {
            PlanId = plan.Id,
            PlanTitle = plan.OriginalRequest,
            TotalSlices = 0,
            AppliedSlices = 0,
            FailedSliceId = null,
            RollbackPerformed = false,
            Success = false,
            StartedAtUtc = DateTime.UtcNow,
            FinishedAtUtc = DateTime.UtcNow,
            StatusMessage = message,
            Summary = message,
            AuditTrail = [$"Plan apply did not start: {message}"],
            SliceResults = []
        };
    }

    private static TaskPlanApplyResult BuildGraphFailureResult(TaskPlan plan, TaskPlanExecutionGraph executionGraph)
    {
        var message = "Dependency graph validation failed.";

        return new TaskPlanApplyResult
        {
            PlanId = plan.Id,
            PlanTitle = plan.OriginalRequest,
            TotalSlices = plan.Slices.Count,
            AppliedSlices = 0,
            FailedSliceId = null,
            RollbackPerformed = false,
            Success = false,
            StartedAtUtc = DateTime.UtcNow,
            FinishedAtUtc = DateTime.UtcNow,
            StatusMessage = message,
            Summary = message,
            AuditTrail =
            [
                "Plan apply did not start: dependency graph validation failed.",
                .. executionGraph.CyclesDetected,
                .. executionGraph.ValidationErrors
            ],
            SliceResults = [],
            ValidationErrors = [.. executionGraph.ValidationErrors],
            CyclesDetected = [.. executionGraph.CyclesDetected],
            OrderedSliceIds = [.. executionGraph.OrderedSliceIds]
        };
    }

    private static string BuildFailureSummary(TaskPlanApplyResult result, TaskPlanSlice failedSlice)
    {
        var applied = result.AppliedSlices;
        var total = result.TotalSlices;
        return result.RollbackPerformed
            ? $"Applied {applied}/{total} approved slice(s). Slice '{failedSlice.Title}' failed and rollback was performed."
            : $"Applied {applied}/{total} approved slice(s). Slice '{failedSlice.Title}' failed.";
    }

    private static void AppendAudit(TaskPlanApplyResult result, string message)
    {
        var items = result.AuditTrail.ToList();
        items.Add(message);
        result.AuditTrail = items;
    }

    private static async Task<TaskPlanApplyResult> ReportAsync(
        TaskPlanApplyResult result,
        Func<TaskPlanApplyResult, Task>? progressReporter,
        CancellationToken cancellationToken)
    {
        if (progressReporter is not null)
        {
            await progressReporter(Clone(result));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static TaskPlanApplyResult Clone(TaskPlanApplyResult result)
    {
        return new TaskPlanApplyResult
        {
            PlanId = result.PlanId,
            PlanTitle = result.PlanTitle,
            TotalSlices = result.TotalSlices,
            AppliedSlices = result.AppliedSlices,
            FailedSliceId = result.FailedSliceId,
            RollbackPerformed = result.RollbackPerformed,
            Success = result.Success,
            CurrentSliceId = result.CurrentSliceId,
            CurrentSliceTitle = result.CurrentSliceTitle,
            CurrentSliceIndex = result.CurrentSliceIndex,
            StatusMessage = result.StatusMessage,
            Summary = result.Summary,
            StartedAtUtc = result.StartedAtUtc,
            FinishedAtUtc = result.FinishedAtUtc,
            AuditTrail = [.. result.AuditTrail],
            SliceResults = [.. result.SliceResults],
            ValidationErrors = [.. result.ValidationErrors],
            CyclesDetected = [.. result.CyclesDetected],
            OrderedSliceIds = [.. result.OrderedSliceIds]
        };
    }
}
