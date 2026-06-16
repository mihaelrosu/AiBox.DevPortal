using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceExecutionService(IWebHostEnvironment environment)
{
    private const string HistoryFileName = "task-slice-execution-history.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<TaskSliceExecutionResult> ExecuteSliceAsync(
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var request = new TaskSliceExecutionRequest
        {
            PlanId = slice.PlanId,
            Slice = slice,
            SliceId = slice.Id,
            SliceTitle = slice.Title,
            RequestedAction = "AdvanceStatus",
            RequestedAt = DateTime.UtcNow,
            RequestedBy = nameof(TaskSliceExecutionService),
            Notes = slice.Notes
        };

        return await ExecuteSliceAsync(request, slice, cancellationToken);
    }

    public async Task<TaskSliceExecutionResult> ExecuteSliceAsync(
        TaskSliceExecutionRequest request,
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(slice);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var executedAt = request.RequestedAt == default ? DateTime.UtcNow : request.RequestedAt;
            var requestedAction = NormalizeRequestedAction(request.RequestedAction);
            var beforeStatus = slice.Status;
            var isGeneratePatch = requestedAction.Equals("GeneratePatch", StringComparison.OrdinalIgnoreCase);
            var canGeneratePatch = beforeStatus is TaskSliceStatus.Pending or TaskSliceStatus.Previewed;
            var afterStatus = isGeneratePatch
                ? (canGeneratePatch ? TaskSliceStatus.Previewed : TaskSliceStatus.Failed)
                : GetNextStatus(beforeStatus);
            var isFailure = afterStatus == TaskSliceStatus.Failed;

            slice.Status = afterStatus;
            slice.UpdatedAt = executedAt;
            slice.Notes = AppendExecutionSummary(
                slice.Notes,
                beforeStatus,
                afterStatus,
                executedAt,
                requestedAction,
                request.RequestedBy,
                request.Notes);

            var result = new TaskSliceExecutionResult
            {
                PlanId = string.IsNullOrWhiteSpace(request.PlanId) ? slice.PlanId : request.PlanId,
                SliceId = string.IsNullOrWhiteSpace(request.SliceId) ? slice.Id : request.SliceId,
                SliceTitle = string.IsNullOrWhiteSpace(request.SliceTitle) ? slice.Title : request.SliceTitle,
                RequestedAction = requestedAction,
                PatchPackageId = slice.PatchPackageId,
                Success = !isFailure,
                BuildSuccess = false,
                VerificationSuccess = false,
                Summary = BuildSummary(slice, beforeStatus, afterStatus, requestedAction, request),
                GeneratedFiles = [],
                Errors = isFailure
                    ? [isGeneratePatch
                        ? $"Slice '{slice.Title}' cannot generate a patch preview from state '{beforeStatus}'."
                        : $"Slice '{slice.Title}' is in a failed state and was not advanced."]
                    : [],
                ExecutedAt = executedAt
            };

            await AppendHistoryAsync(result, cancellationToken);
            return Clone(result);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static TaskSliceStatus GetNextStatus(TaskSliceStatus status)
    {
        return status switch
        {
            TaskSliceStatus.Pending => TaskSliceStatus.Previewed,
            TaskSliceStatus.Previewed => TaskSliceStatus.Applied,
            TaskSliceStatus.Applied => TaskSliceStatus.Verified,
            TaskSliceStatus.Verified => TaskSliceStatus.Verified,
            TaskSliceStatus.Failed => TaskSliceStatus.Failed,
            TaskSliceStatus.RolledBack => TaskSliceStatus.RolledBack,
            _ => TaskSliceStatus.Previewed
        };
    }

    private static string BuildSummary(
        TaskPlanSlice slice,
        TaskSliceStatus beforeStatus,
        TaskSliceStatus afterStatus,
        string requestedAction,
        TaskSliceExecutionRequest request)
    {
        var requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "unknown caller" : request.RequestedBy;
        return $"{requestedAction} for {slice.Title} by {requestedBy}: {beforeStatus} -> {afterStatus} at {DateTime.UtcNow:O}. No build, patch, or verification work was executed.";
    }

    private static string AppendExecutionSummary(
        string? notes,
        TaskSliceStatus beforeStatus,
        TaskSliceStatus afterStatus,
        DateTime executedAt,
        string requestedAction,
        string requestedBy,
        string requestNotes)
    {
        var summaryLine = $"[{executedAt:O}] {requestedAction} by {(string.IsNullOrWhiteSpace(requestedBy) ? "unknown caller" : requestedBy)}: {beforeStatus} -> {afterStatus}";

        var mergedNotes = string.IsNullOrWhiteSpace(notes)
            ? summaryLine
            : $"{notes.Trim()}{Environment.NewLine}{summaryLine}";

        return string.IsNullOrWhiteSpace(requestNotes)
            ? mergedNotes
            : $"{mergedNotes}{Environment.NewLine}{requestNotes.Trim()}";
    }

    private static string NormalizeRequestedAction(string? requestedAction)
    {
        return string.IsNullOrWhiteSpace(requestedAction) ? "AdvanceStatus" : requestedAction.Trim();
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
            PatchPackageId = result.PatchPackageId,
            Success = result.Success,
            BuildSuccess = result.BuildSuccess,
            VerificationSuccess = result.VerificationSuccess,
            Summary = result.Summary,
            GeneratedFiles = [.. result.GeneratedFiles ?? []],
            Errors = [.. result.Errors ?? []],
            ExecutedAt = result.ExecutedAt
        };
    }
}
