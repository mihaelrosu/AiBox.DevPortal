using System.Diagnostics;
using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApplyService(
    IPatchBackupService patchBackupService,
    IPatchPackageService patchPackageService,
    IPatchRollbackService patchRollbackService,
    TaskSliceRollbackService taskSliceRollbackService,
    TaskSliceApplyHistoryService taskSliceApplyHistoryService,
    TaskSliceApprovalService taskSliceApprovalService,
    TaskSliceApplyAuditService taskSliceApplyAuditService)
{
    public async Task<TaskSliceApplyResult> ApplyAsync(
        string projectPath,
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        return await ApplyAsync(projectPath, slice, false, cancellationToken);
    }

    public async Task<TaskSliceApplyResult> ApplyAsync(
        string projectPath,
        TaskPlanSlice slice,
        bool highRiskApproved,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            var result = new TaskSliceApplyResult
            {
                Success = false,
                Message = "Project path is required.",
                Errors = ["Project path is required."]
            };

            if (slice is null)
            {
                return result;
            }

            return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(slice);

        if (slice.Status != TaskSliceStatus.Verified)
        {
            var error = $"Slice '{slice.Title}' must be in Verified status before apply.";
            var result = new TaskSliceApplyResult
            {
                Success = false,
                Message = error,
                Errors = [error]
            };

            return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
        }

        var riskGate = EvaluateRiskGate(slice, highRiskApproved);
        var riskGateMessage = riskGate.Message;
        if (!riskGate.Allowed)
        {
            var errorMessage = riskGate.Message ?? "Slice is blocked by the risk gate.";
            var result = new TaskSliceApplyResult
            {
                Success = false,
                Message = errorMessage,
                RiskGateMessage = errorMessage,
                Errors = [errorMessage]
            };

            return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(slice.PatchPackageId))
        {
            var error = $"Slice '{slice.Title}' does not have a linked patch package.";
            var result = new TaskSliceApplyResult
            {
                Success = false,
                Message = error,
                Errors = [error]
            };

            return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
        }

        try
        {
            var approvalStatus = await taskSliceApprovalService.GetStatusAsync(slice, cancellationToken);
            if (approvalStatus != TaskSliceApprovalStatus.Approved)
            {
                var error = approvalStatus == TaskSliceApprovalStatus.Rejected
                    ? $"Slice '{slice.Title}' was rejected and cannot be applied."
                    : $"Slice '{slice.Title}' must be approved before apply.";
                var result = new TaskSliceApplyResult
                {
                    Success = false,
                    Message = error,
                    Errors = [error]
                };

                return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
            }

            var package = await patchPackageService.GetByIdAsync(slice.PatchPackageId, cancellationToken);
            if (package is null)
            {
                var error = $"Patch package not found: {slice.PatchPackageId}";
                var result = new TaskSliceApplyResult
                {
                    Success = false,
                    Message = error,
                    Errors = [error]
                };

                return await CompleteAttemptAsync(slice, result, blocked: true, highRiskApproved: highRiskApproved, changedFiles: [], cancellationToken);
            }

            var rootPath = GetValidatedProjectRoot(projectPath);
            var fileChanges = package.FileChanges?.ToArray() ?? [];
            var validatedPaths = new List<string>(fileChanges.Length);

            foreach (var change in fileChanges)
            {
                var targetPath = GetValidatedTargetPath(rootPath, change.RelativePath);
                validatedPaths.Add(Path.GetRelativePath(rootPath, targetPath).Replace('\\', '/'));
            }
            var changedFiles = validatedPaths.ToArray();

            var backupResult = await patchBackupService.CreateBackupAsync(
                rootPath,
                validatedPaths,
                cancellationToken);

            await patchRollbackService.RecordAsync(
                new PatchRollbackEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    PlanId = slice.PlanId,
                    SliceId = slice.Id,
                    BackupId = backupResult.BackupFolderPath,
                    ChangedFiles = [.. validatedPaths],
                    Restored = false
                },
                cancellationToken);

            var appliedFilesCount = await ApplyPackageFileChangesAsync(rootPath, fileChanges, cancellationToken);
            var appliedAt = DateTime.UtcNow;

            package.BackupFolder = backupResult.BackupFolderPath;
            package.AppliedAt = DateTimeOffset.UtcNow;
            package.RolledBack = false;
            package.RolledBackAt = null;
            package.RollbackResult = string.Empty;
            package.RollbackError = string.Empty;
            package.Status = PatchPackageStatus.Applied;
            package.StatusMessage = $"Applied with {backupResult.BackedUpFilesCount} backup file(s).";
            package.UpdatedAt = package.AppliedAt.Value;
            await patchPackageService.SaveAsync(package, cancellationToken);

            slice.Status = TaskSliceStatus.Applied;
            slice.AppliedAt = appliedAt;
            slice.UpdatedAt = appliedAt;

            var buildProcessResult = await RunDotNetBuildAsync(rootPath, cancellationToken);
            var buildVerificationResult = BuildVerificationResult(buildProcessResult);

            TaskSliceApplyResult applyResult;

            if (buildVerificationResult.Success)
            {
                slice.Status = TaskSliceStatus.Applied;
                slice.AppliedAt = buildVerificationResult.VerifiedAtUtc;
                slice.UpdatedAt = buildVerificationResult.VerifiedAtUtc;

                applyResult = new TaskSliceApplyResult
                {
                    Success = true,
                    Message = "Applied successfully",
                    RiskGateMessage = riskGateMessage,
                    BackupFolderPath = backupResult.BackupFolderPath,
                    BackedUpFilesCount = backupResult.BackedUpFilesCount,
                    AppliedFilesCount = appliedFilesCount,
                    BuildVerificationResult = buildVerificationResult,
                    Errors = []
                };
                return await CompleteAttemptAsync(slice, applyResult, blocked: false, highRiskApproved: highRiskApproved, changedFiles, cancellationToken);
            }

            var buildFailureErrors = BuildErrors(buildVerificationResult);

            var rollbackResult = await taskSliceRollbackService.RollbackSliceAsync(slice, cancellationToken);
            var rollbackSucceeded = rollbackResult.Success;
            var rollbackMessage = rollbackSucceeded
                ? "Rollback completed successfully."
                : "Rollback failed.";

            string? rollbackBuildMessage = null;
            if (rollbackSucceeded)
            {
                slice.AppliedAt = null;
                var rollbackBuildResult = await RunDotNetBuildAsync(rootPath, cancellationToken);
                var rollbackBuildVerification = BuildVerificationResult(rollbackBuildResult);
                rollbackBuildMessage = rollbackBuildVerification.Success
                    ? "Post-rollback build succeeded."
                    : $"Post-rollback build failed with exit code {rollbackBuildVerification.ExitCode}.";
                slice.UpdatedAt = rollbackBuildVerification.VerifiedAtUtc;
            }
            else
            {
                slice.Status = TaskSliceStatus.Failed;
                slice.AppliedAt = null;
                slice.UpdatedAt = DateTime.UtcNow;
            }

            applyResult = new TaskSliceApplyResult
            {
                Success = false,
                RolledBack = rollbackSucceeded,
                RollbackSucceeded = rollbackSucceeded,
                Message = rollbackSucceeded
                    ? "Build failed. Rollback completed successfully."
                    : "Build failed. Rollback failed.",
                RiskGateMessage = riskGateMessage,
                RollbackMessage = rollbackSucceeded
                    ? string.IsNullOrWhiteSpace(rollbackBuildMessage)
                        ? rollbackMessage
                        : $"{rollbackMessage} {rollbackBuildMessage}"
                    : rollbackResult.Summary,
                BackupFolderPath = backupResult.BackupFolderPath,
                BackedUpFilesCount = backupResult.BackedUpFilesCount,
                AppliedFilesCount = appliedFilesCount,
                BuildVerificationResult = buildVerificationResult,
                Errors = buildFailureErrors
            };
            return await CompleteAttemptAsync(slice, applyResult, blocked: false, highRiskApproved: highRiskApproved, changedFiles, cancellationToken);
        }
        catch (Exception exception)
        {
            var blocked = !slice.Status.Equals(TaskSliceStatus.Applied);
            var failedAt = DateTime.UtcNow;
            slice.Status = TaskSliceStatus.Failed;
            slice.AppliedAt = null;
            slice.UpdatedAt = failedAt;

            var result = new TaskSliceApplyResult
            {
                Success = false,
                RolledBack = false,
                RollbackSucceeded = false,
                Message = $"Apply simulation failed: {exception.Message}",
                RiskGateMessage = riskGateMessage,
                RollbackMessage = exception.Message,
                Errors = [exception.Message]
            };

            return await CompleteAttemptAsync(slice, result, blocked, highRiskApproved, [], cancellationToken);
        }
    }

    private static async Task<int> ApplyPackageFileChangesAsync(
        string rootPath,
        IReadOnlyList<PatchFileChange> fileChanges,
        CancellationToken cancellationToken)
    {
        var appliedFiles = 0;

        foreach (var change in fileChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = GetValidatedTargetPath(rootPath, change.RelativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await File.WriteAllTextAsync(
                targetPath,
                change.NewContent ?? string.Empty,
                new UTF8Encoding(false),
                cancellationToken);

            appliedFiles++;
        }

        return appliedFiles;
    }

    private static async Task<BuildProcessResult> RunDotNetBuildAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build",
            WorkingDirectory = projectPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            return new BuildProcessResult(-1, string.Empty, "Failed to start dotnet build.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        return new BuildProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static TaskSliceBuildVerificationResult BuildVerificationResult(BuildProcessResult buildProcessResult)
    {
        var output = BuildOutput(buildProcessResult.StandardOutput, buildProcessResult.StandardError);

        return new TaskSliceBuildVerificationResult
        {
            Success = buildProcessResult.ExitCode == 0,
            ExitCode = buildProcessResult.ExitCode,
            Output = output,
            VerifiedAtUtc = DateTime.UtcNow
        };
    }

    private static string BuildOutput(string standardOutput, string standardError)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            builder.AppendLine("stdout:");
            builder.AppendLine(standardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine("stderr:");
            builder.AppendLine(standardError.TrimEnd());
        }

        return builder.ToString().Trim();
    }

    private static string GetValidatedProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("Project path is required.");
        }

        var fullPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Project path does not exist: {fullPath}");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string GetValidatedTargetPath(string rootPath, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid patch file path: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalized));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Patch file path is outside the project root: {relativePath}");
        }

        return fullPath;
    }

    private static IReadOnlyList<string> BuildErrors(TaskSliceBuildVerificationResult buildVerificationResult)
    {
        var errors = new List<string>
        {
            $"dotnet build failed with exit code {buildVerificationResult.ExitCode}."
        };

        if (!string.IsNullOrWhiteSpace(buildVerificationResult.Output))
        {
            errors.Add(buildVerificationResult.Output);
        }

        return errors;
    }

    private async Task RecordHistoryAsync(
        TaskPlanSlice slice,
        TaskSliceApplyResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await taskSliceApplyHistoryService.AddAsync(
                new TaskSliceApplyHistoryItem
                {
                    PlanId = slice.PlanId,
                    SliceId = slice.Id,
                    SliceTitle = slice.Title,
                    PatchPackageId = slice.PatchPackageId,
                    AppliedAtUtc = DateTime.UtcNow,
                    BackupFolderPath = result.BackupFolderPath,
                    BackedUpFilesCount = result.BackedUpFilesCount,
                    AppliedFilesCount = result.AppliedFilesCount,
                    BuildSucceeded = result.BuildVerificationResult?.Success == true,
                    RolledBack = result.RolledBack,
                    RollbackSucceeded = result.RollbackSucceeded,
                    ResultMessage = BuildHistoryMessage(result)
                },
                cancellationToken);
        }
        catch
        {
            // History tracking must not block the apply flow.
        }
    }

    private async Task RecordAuditAsync(
        TaskPlanSlice slice,
        TaskSliceApplyResult result,
        bool blocked,
        bool approvedHighRiskApply,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        try
        {
            await taskSliceApplyAuditService.AddAsync(
                new TaskSliceApplyAuditEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    PlanId = slice.PlanId,
                    SliceId = slice.Id,
                    SliceTitle = slice.Title,
                    RiskLevel = slice.RiskLevel,
                    RiskScore = slice.RiskScore,
                    ApprovedHighRiskApply = approvedHighRiskApply,
                    Success = result.Success,
                    Blocked = blocked,
                    Message = BuildAuditMessage(result),
                    ChangedFiles = [.. changedFiles]
                },
                cancellationToken);
        }
        catch
        {
            // Audit tracking must not block the apply flow.
        }
    }

    private async Task<TaskSliceApplyResult> CompleteAttemptAsync(
        TaskPlanSlice slice,
        TaskSliceApplyResult result,
        bool blocked,
        bool highRiskApproved,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        await RecordHistoryAsync(slice, result, cancellationToken);
        await RecordAuditAsync(slice, result, blocked, highRiskApproved, changedFiles, cancellationToken);
        return result;
    }

    private static string BuildHistoryMessage(TaskSliceApplyResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.RollbackMessage)
            ? result.Message
            : $"{result.Message} {result.RollbackMessage}".Trim();

        if (string.IsNullOrWhiteSpace(result.RiskGateMessage))
        {
            return message;
        }

        if (string.Equals(message, result.RiskGateMessage, StringComparison.Ordinal))
        {
            return message;
        }

        return $"{message} {result.RiskGateMessage}".Trim();
    }

    private static string BuildAuditMessage(TaskSliceApplyResult result)
    {
        var message = BuildHistoryMessage(result);
        return string.IsNullOrWhiteSpace(message) ? result.Message : message;
    }

    private static RiskGateOutcome EvaluateRiskGate(TaskPlanSlice slice, bool highRiskApproved)
    {
        return slice.RiskLevel switch
        {
            RiskLevel.Low => new RiskGateOutcome(true, null),
            RiskLevel.Medium => new RiskGateOutcome(true, "Medium-risk slice. Review carefully before apply."),
            RiskLevel.High when highRiskApproved => new RiskGateOutcome(true, "High-risk apply approved."),
            RiskLevel.High => new RiskGateOutcome(false, "High-risk slices require explicit manual approval before apply."),
            RiskLevel.Critical => new RiskGateOutcome(false, "Critical-risk slices cannot be applied."),
            _ => new RiskGateOutcome(true, null)
        };
    }

    private sealed record BuildProcessResult(int ExitCode, string StandardOutput, string StandardError);
    private sealed record RiskGateOutcome(bool Allowed, string? Message);
}
