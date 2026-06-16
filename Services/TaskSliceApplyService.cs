using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApplyService(
    IPatchApplyService patchApplyService)
{
    public async Task<TaskSliceExecutionResult> ApplySliceAsync(
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Status != TaskSliceStatus.Verified)
        {
            var appliedAt = DateTime.UtcNow;
            var failureResult = new TaskSliceExecutionResult
            {
                PlanId = slice.PlanId,
                SliceId = slice.Id,
                SliceTitle = slice.Title,
                RequestedAction = "Apply",
                PatchPackageId = slice.PatchPackageId,
                BackupId = string.Empty,
                Success = false,
                BuildSuccess = false,
                VerificationSuccess = false,
                AppliedFiles = [],
                AppliedAt = null,
                Summary = $"{slice.Title}: {slice.Status} -> Failed at {appliedAt:O}. Slice must be Verified before apply.",
                Errors = [$"Slice '{slice.Title}' must be in Verified status before apply."],
                ExecutedAt = appliedAt
            };

            slice.Status = TaskSliceStatus.Failed;
            slice.UpdatedAt = appliedAt;
            return failureResult;
        }

        if (string.IsNullOrWhiteSpace(slice.PatchPackageId))
        {
            var appliedAt = DateTime.UtcNow;
            var failureResult = new TaskSliceExecutionResult
            {
                PlanId = slice.PlanId,
                SliceId = slice.Id,
                SliceTitle = slice.Title,
                RequestedAction = "Apply",
                PatchPackageId = string.Empty,
                BackupId = string.Empty,
                Success = false,
                BuildSuccess = false,
                VerificationSuccess = false,
                AppliedFiles = [],
                AppliedAt = null,
                Summary = $"{slice.Title}: {slice.Status} -> Failed at {appliedAt:O}. Slice must have a linked PatchPackageId before apply.",
                Errors = [$"Slice '{slice.Title}' does not have a linked patch package."],
                ExecutedAt = appliedAt
            };

            slice.Status = TaskSliceStatus.Failed;
            slice.UpdatedAt = appliedAt;
            return failureResult;
        }

        var appliedPackage = await patchApplyService.ApplyAsync(slice.PatchPackageId, cancellationToken);
        var appliedFiles = appliedPackage.FileChanges
            .Select(change => change.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var appliedAtValue = appliedPackage.AppliedAt?.UtcDateTime ?? DateTime.UtcNow;

        slice.Status = TaskSliceStatus.Applied;
        slice.UpdatedAt = appliedAtValue;

        return new TaskSliceExecutionResult
        {
            PlanId = slice.PlanId,
            SliceId = slice.Id,
            SliceTitle = slice.Title,
            RequestedAction = "Apply",
            PatchPackageId = appliedPackage.Id,
            BackupId = appliedPackage.BackupFolder,
            Success = true,
            BuildSuccess = false,
            VerificationSuccess = false,
            AppliedFiles = appliedFiles,
            AppliedAt = appliedPackage.AppliedAt?.UtcDateTime ?? appliedAtValue,
            Summary = $"{slice.Title}: Verified -> Applied at {appliedAtValue:O}. Applied linked patch package {appliedPackage.Id}.",
            Errors = [],
            ExecutedAt = appliedAtValue
        };
    }
}
