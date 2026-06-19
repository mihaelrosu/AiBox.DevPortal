using System.Diagnostics;
using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceApplyService(
    IPatchBackupService patchBackupService,
    IPatchPackageService patchPackageService,
    TaskSliceRollbackService taskSliceRollbackService,
    TaskSliceApplyHistoryService taskSliceApplyHistoryService,
    TaskSliceApprovalService taskSliceApprovalService)
{
    public async Task<TaskSliceApplyResult> ApplyAsync(
        string projectPath,
        TaskPlanSlice slice,
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

            if (slice is not null)
            {
                await RecordHistoryAsync(slice, result, cancellationToken);
            }
            return result;
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

            if (slice is not null)
            {
                await RecordHistoryAsync(slice, result, cancellationToken);
            }
            return result;
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

            if (slice is not null)
            {
                await RecordHistoryAsync(slice, result, cancellationToken);
            }
            return result;
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

                await RecordHistoryAsync(slice, result, cancellationToken);
                return result;
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

                if (slice is not null)
                {
                    await RecordHistoryAsync(slice, result, cancellationToken);
                }
                return result;
            }

            var rootPath = GetValidatedProjectRoot(projectPath);
            var fileChanges = package.FileChanges?.ToArray() ?? [];
            var validatedPaths = new List<string>(fileChanges.Length);

            foreach (var change in fileChanges)
            {
                var targetPath = GetValidatedTargetPath(rootPath, change.RelativePath);
                validatedPaths.Add(Path.GetRelativePath(rootPath, targetPath).Replace('\\', '/'));
            }

            var backupResult = await patchBackupService.CreateBackupAsync(
                rootPath,
                validatedPaths,
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
                    BackupFolderPath = backupResult.BackupFolderPath,
                    BackedUpFilesCount = backupResult.BackedUpFilesCount,
                    AppliedFilesCount = appliedFilesCount,
                    BuildVerificationResult = buildVerificationResult,
                    Errors = []
                };
                await RecordHistoryAsync(slice, applyResult, cancellationToken);
                return applyResult;
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
            await RecordHistoryAsync(slice, applyResult, cancellationToken);
            return applyResult;
        }
        catch (Exception exception)
        {
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
                RollbackMessage = exception.Message,
                Errors = [exception.Message]
            };

            await RecordHistoryAsync(slice, result, cancellationToken);
            return result;
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

    private static string BuildHistoryMessage(TaskSliceApplyResult result)
    {
        if (string.IsNullOrWhiteSpace(result.RollbackMessage))
        {
            return result.Message;
        }

        return $"{result.Message} {result.RollbackMessage}".Trim();
    }

    private sealed record BuildProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
