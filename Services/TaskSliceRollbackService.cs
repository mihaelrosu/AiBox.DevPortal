using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceRollbackService(
    IPatchRollbackService patchRollbackService)
{
    public async Task<TaskSliceExecutionResult> RollbackSliceAsync(
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var executedAt = DateTime.UtcNow;
        if (slice.Status != TaskSliceStatus.Applied)
        {
            return BuildFailureResult(
                slice,
                executedAt,
                $"Slice '{slice.Title}' must be in Applied status before rollback.",
                $"Slice '{slice.Title}' must be in Applied status before rollback.");
        }

        if (string.IsNullOrWhiteSpace(slice.PatchPackageId))
        {
            return BuildFailureResult(
                slice,
                executedAt,
                $"Slice '{slice.Title}' does not have a linked patch package.",
                $"Slice '{slice.Title}' does not have a linked patch package.");
        }

        try
        {
            var rolledBackPackage = await patchRollbackService.RollbackAsync(slice.PatchPackageId, cancellationToken);
            var rolledBackAt = rolledBackPackage.RolledBackAt?.UtcDateTime ?? DateTime.UtcNow;

            slice.Status = TaskSliceStatus.RolledBack;
            slice.RolledBackAt = rolledBackAt;
            slice.UpdatedAt = rolledBackAt;

            return new TaskSliceExecutionResult
            {
                PlanId = slice.PlanId,
                SliceId = slice.Id,
                SliceTitle = slice.Title,
                RequestedAction = "Rollback",
                PatchPackageId = rolledBackPackage.Id,
                BackupId = rolledBackPackage.BackupFolder,
                Success = true,
                BuildSuccess = false,
                VerificationSuccess = false,
                AppliedFiles = [],
                AppliedAt = null,
                Summary = $"{slice.Title}: Applied -> RolledBack at {rolledBackAt:O}. Rolled back linked patch package {rolledBackPackage.Id}.",
                Errors = [],
                ExecutedAt = rolledBackAt
            };
        }
        catch (Exception exception)
        {
            slice.Status = TaskSliceStatus.Failed;
            slice.RolledBackAt = null;
            slice.UpdatedAt = executedAt;

            return BuildFailureResult(
                slice,
                executedAt,
                $"Slice '{slice.Title}' rollback failed: {exception.Message}",
                exception.Message);
        }
    }

    private static TaskSliceExecutionResult BuildFailureResult(
        TaskPlanSlice slice,
        DateTime executedAt,
        string summary,
        string error)
    {
        return new TaskSliceExecutionResult
        {
            PlanId = slice.PlanId,
            SliceId = slice.Id,
            SliceTitle = slice.Title,
            RequestedAction = "Rollback",
            PatchPackageId = slice.PatchPackageId,
            BackupId = string.Empty,
            Success = false,
            BuildSuccess = false,
            VerificationSuccess = false,
            AppliedFiles = [],
            AppliedAt = null,
            Summary = summary,
            Errors = [error],
            ExecutedAt = executedAt
        };
    }
}
