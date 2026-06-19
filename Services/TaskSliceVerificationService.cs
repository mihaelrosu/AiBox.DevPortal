using System.Diagnostics;
using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceVerificationService(IWebHostEnvironment environment)
{
    private const string HistoryFileName = "task-slice-execution-history.json";
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public static bool CanVerify(TaskPlanSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        return slice.Status == TaskSliceStatus.Previewed;
    }

    public async Task<TaskSliceExecutionResult> VerifySliceAsync(
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var verifiedAt = DateTime.UtcNow;
            var beforeStatus = slice.Status;
            if (!CanVerify(slice))
            {
                var failureProcessResult = new ProcessResult(
                    -1,
                    string.Empty,
                    $"Slice '{slice.Title}' must be in Previewed status before verification.",
                    false);
                var failed = BuildResult(
                    slice,
                    beforeStatus,
                    TaskSliceStatus.Failed,
                    verifiedAt,
                    slice.PlanId,
                    slice.Title,
                    "Verify",
                    failureProcessResult);
                slice.Status = TaskSliceStatus.Failed;
                slice.UpdatedAt = verifiedAt;
                slice.Notes = AppendVerificationSummary(slice.Notes, beforeStatus, TaskSliceStatus.Failed, verifiedAt, failureProcessResult.ExitCode, failureProcessResult.TimedOut);

                await AppendHistoryAsync(failed, cancellationToken);
                return Clone(failed);
            }

            var processResult = await RunDotNetBuildAsync(cancellationToken);
            var afterStatus = processResult.ExitCode == 0 && !processResult.TimedOut
                ? TaskSliceStatus.Verified
                : TaskSliceStatus.Failed;

            var result = BuildResult(slice, beforeStatus, afterStatus, verifiedAt, slice.PlanId, slice.Title, "Verify", processResult);
            slice.Status = afterStatus;
            slice.UpdatedAt = verifiedAt;
            slice.Notes = AppendVerificationSummary(slice.Notes, beforeStatus, afterStatus, verifiedAt, processResult.ExitCode, processResult.TimedOut);

            await AppendHistoryAsync(result, cancellationToken);
            return Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<ProcessResult> RunDotNetBuildAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build",
            WorkingDirectory = environment.ContentRootPath,
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
            return new ProcessResult(-1, string.Empty, "Failed to start dotnet build.", false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = !await WaitForExitAsync(process, cancellationToken);
        if (timedOut && !process.HasExited)
        {
            TryKill(process);
            await process.WaitForExitAsync(cancellationToken);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return new ProcessResult(process.HasExited ? process.ExitCode : -1, stdout, stderr, timedOut);
    }

    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(BuildTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static TaskSliceExecutionResult BuildResult(
        TaskPlanSlice slice,
        TaskSliceStatus beforeStatus,
        TaskSliceStatus afterStatus,
        DateTime verifiedAt,
        string planId,
        string sliceTitle,
        string requestedAction,
        ProcessResult processResult)
    {
        var success = afterStatus == TaskSliceStatus.Verified;
        var summary = BuildSummary(slice, beforeStatus, afterStatus, verifiedAt, processResult);
        var errors = success ? [] : BuildErrors(slice, processResult);

        return new TaskSliceExecutionResult
        {
            PlanId = planId,
            SliceId = slice.Id,
            SliceTitle = sliceTitle,
            RequestedAction = requestedAction,
            Success = success,
            BuildSuccess = success,
            VerificationSuccess = success,
            Summary = summary,
            GeneratedFiles = [],
            Errors = errors,
            ExecutedAt = verifiedAt
        };
    }

    private static string BuildSummary(
        TaskPlanSlice slice,
        TaskSliceStatus beforeStatus,
        TaskSliceStatus afterStatus,
        DateTime verifiedAt,
        ProcessResult processResult)
    {
        var outcome = processResult.TimedOut
            ? "timed out"
            : $"exit code {processResult.ExitCode}";

        return $"{slice.Title}: {beforeStatus} -> {afterStatus} at {verifiedAt:O}. dotnet build finished with {outcome}.";
    }

    private static List<string> BuildErrors(TaskPlanSlice slice, ProcessResult processResult)
    {
        var errors = new List<string>();

        if (processResult.TimedOut)
        {
            errors.Add($"Slice '{slice.Title}' verification timed out after {BuildTimeout.TotalMinutes:N0} minutes.");
        }
        else
        {
            errors.Add($"Slice '{slice.Title}' verification failed with exit code {processResult.ExitCode}.");
        }

        if (!string.IsNullOrWhiteSpace(processResult.StandardError))
        {
            errors.Add(FormatOutput("stderr", processResult.StandardError));
        }

        if (!string.IsNullOrWhiteSpace(processResult.StandardOutput))
        {
            errors.Add(FormatOutput("stdout", processResult.StandardOutput));
        }

        return errors;
    }

    private static string FormatOutput(string label, string value)
    {
        const int maxLength = 1200;
        var text = value.Trim();
        if (text.Length <= maxLength)
        {
            return $"{label}: {text}";
        }

        return $"{label}: {text[..maxLength]}...";
    }

    private static string AppendVerificationSummary(
        string? notes,
        TaskSliceStatus beforeStatus,
        TaskSliceStatus afterStatus,
        DateTime verifiedAt,
        int? exitCode,
        bool timedOut)
    {
        var outcome = timedOut ? "timed out" : $"exit code {exitCode}";
        var summaryLine = $"[{verifiedAt:O}] verify: {beforeStatus} -> {afterStatus} ({outcome})";

        return string.IsNullOrWhiteSpace(notes)
            ? summaryLine
            : $"{notes.Trim()}{Environment.NewLine}{summaryLine}";
    }

    private async Task AppendHistoryAsync(TaskSliceExecutionResult result, CancellationToken cancellationToken)
    {
        var history = await LoadHistoryAsync(cancellationToken);
        history.Add(Clone(result));
        await SaveHistoryAsync(history, cancellationToken);
    }

    private async Task<List<TaskSliceExecutionResult>> LoadHistoryAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<TaskSliceExecutionResult>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveHistoryAsync(List<TaskSliceExecutionResult> history, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, history, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", HistoryFileName);
    }

    private static TaskSliceExecutionResult Clone(TaskSliceExecutionResult result)
    {
        return new TaskSliceExecutionResult
        {
            PlanId = result.PlanId,
            SliceId = result.SliceId,
            SliceTitle = result.SliceTitle,
            RequestedAction = result.RequestedAction,
            Success = result.Success,
            BuildSuccess = result.BuildSuccess,
            VerificationSuccess = result.VerificationSuccess,
            Summary = result.Summary,
            GeneratedFiles = [.. result.GeneratedFiles ?? []],
            Errors = [.. result.Errors ?? []],
            ExecutedAt = result.ExecutedAt
        };
    }

    private readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);
}
