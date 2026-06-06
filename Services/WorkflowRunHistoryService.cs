using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class WorkflowRunHistoryService(
    IWorkflowRunPreviewService previewService,
    ILocalLlmService localLlmService) : IWorkflowRunHistoryService
{
    private const string HistoryPath = "/data/workflow-runs.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<WorkflowRunRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var runs = await LoadAsync(cancellationToken);
            return runs
                .OrderByDescending(run => run.Created)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowRunRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var runs = await LoadAsync(cancellationToken);
            var run = runs.FirstOrDefault(run => run.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            return run is null ? null : Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowRunRecord?> CreateAsync(
        WorkflowRunPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var preview = await previewService.PreviewAsync(request, cancellationToken);

        if (preview is null)
        {
            return null;
        }

        var run = new WorkflowRunRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkflowId = preview.WorkflowId,
            WorkflowName = preview.WorkflowName,
            GoalText = preview.GoalText,
            ProjectName = preview.ProjectName,
            Created = DateTimeOffset.UtcNow,
            Status = WorkflowRunStatus.Planned,
            Steps = preview.Steps
                .OrderBy(step => step.Order)
                .Select(step => new WorkflowRunStepRecord
                {
                    StepId = step.StepId,
                    Order = step.Order,
                    Enabled = step.Enabled,
                    StepName = step.StepName,
                    AgentId = step.AgentId,
                    AgentName = step.AgentName,
                    Model = step.Model,
                    Instruction = step.Instruction,
                    GeneratedTaskPrompt = step.GeneratedTaskPrompt,
                    IncludePreviousResults = step.IncludePreviousResults,
                    PreviousResultMode = step.PreviousResultMode,
                    DependsOnStepIds = [.. (step.DependsOnStepIds ?? [])],
                    Status = WorkflowRunStepStatus.Planned
                })
                .ToList()
        };

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var runs = await LoadAsync(cancellationToken);
            runs.Add(run);
            await SaveAsync(runs, cancellationToken);
            return Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var runs = await LoadAsync(cancellationToken);
            var removed = runs.RemoveAll(run => run.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                await SaveAsync(runs, cancellationToken);
            }

            return removed;
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<WorkflowRunRecord?> UpdateStepResultAsync(
        string runId,
        string stepId,
        WorkflowRunStepResultUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await UpdateStepAsync(runId, stepId, step =>
        {
            var now = DateTimeOffset.UtcNow;
            step.ResultText = request.ResultText?.Trim() ?? string.Empty;
            step.Notes = request.Notes?.Trim() ?? string.Empty;
            step.Status = WorkflowRunStepStatus.Completed;
            step.StartedAt ??= now;
            step.CompletedAt = now;
        }, cancellationToken);
    }

    public Task<WorkflowRunRecord?> UpdateStepStatusAsync(
        string runId,
        string stepId,
        WorkflowRunStepStatus status,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Workflow step status is invalid.", nameof(status));
        }

        return UpdateStepAsync(runId, stepId, step => ApplyStatus(step, status), cancellationToken);
    }

    public async Task<WorkflowRunRecord?> ExecuteStepWithLocalLlmAsync(
        string runId,
        string stepId,
        CancellationToken cancellationToken = default)
    {
        var run = await GetByIdAsync(runId, cancellationToken);
        var step = run?.Steps.FirstOrDefault(step => step.StepId.Equals(stepId, StringComparison.OrdinalIgnoreCase));

        if (run is null || step is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(step.Model))
        {
            throw new ArgumentException($"Workflow step '{step.StepName}' does not specify a model.", nameof(stepId));
        }

        step.GeneratedTaskPrompt = WorkflowPromptBuilder.Build(run, step);
        run = await UpdateStepPromptAsync(runId, stepId, step.GeneratedTaskPrompt, cancellationToken);

        if (run is null)
        {
            return null;
        }

        var runningRun = await UpdateStepStatusAsync(runId, stepId, WorkflowRunStepStatus.Running, cancellationToken);

        if (runningRun is null)
        {
            return null;
        }

        try
        {
            var result = await localLlmService.GenerateAsync(step.Model, step.GeneratedTaskPrompt, cancellationToken);
            return await UpdateStepResultAsync(runId, stepId, new WorkflowRunStepResultUpdateRequest
            {
                ResultText = result,
                Notes = step.Notes
            }, cancellationToken);
        }
        catch
        {
            try
            {
                await UpdateStepStatusAsync(runId, stepId, WorkflowRunStepStatus.Failed, CancellationToken.None);
            }
            catch
            {
                // Preserve the original local LLM failure for the caller.
            }

            throw;
        }
    }

    public async Task<WorkflowRunRecord?> ExecuteAllWithLocalLlmAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var run = await GetByIdAsync(runId, cancellationToken);

        if (run is null)
        {
            return null;
        }

        // Safety: this only calls local Ollama. It must not execute shell commands or
        // modify files except the workflow run history JSON persisted by this service.
        foreach (var runStep in run.Steps.OrderBy(step => step.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!runStep.Enabled)
            {
                await UpdateStepStatusAsync(runId, runStep.StepId, WorkflowRunStepStatus.Skipped, cancellationToken);
                continue;
            }

            run = await GetByIdAsync(runId, cancellationToken);
            var step = run?.Steps.FirstOrDefault(step => step.StepId.Equals(runStep.StepId, StringComparison.OrdinalIgnoreCase));

            if (run is null || step is null)
            {
                return null;
            }

            step.GeneratedTaskPrompt = WorkflowPromptBuilder.Build(run, step);
            run = await UpdateStepPromptAsync(runId, step.StepId, step.GeneratedTaskPrompt, cancellationToken);

            if (run is null)
            {
                return null;
            }

            var runningRun = await UpdateStepStatusAsync(runId, step.StepId, WorkflowRunStepStatus.Running, cancellationToken);

            if (runningRun is null)
            {
                return null;
            }

            try
            {
                ValidateExecutableStep(step);
                var result = await localLlmService.GenerateAsync(step.Model, step.GeneratedTaskPrompt, cancellationToken);
                var completedRun = await UpdateStepResultAsync(runId, step.StepId, new WorkflowRunStepResultUpdateRequest
                {
                    ResultText = result,
                    Notes = string.Empty
                }, cancellationToken);

                if (completedRun is null)
                {
                    return null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryUpdateStepAsync(runId, step.StepId, currentStep =>
                {
                    ApplyStatus(currentStep, WorkflowRunStepStatus.Planned);
                });
                throw;
            }
            catch (Exception exception)
            {
                await TryUpdateStepAsync(runId, step.StepId, currentStep =>
                {
                    ApplyStatus(currentStep, WorkflowRunStepStatus.Failed);
                    currentStep.Notes = exception.Message;
                });
                throw;
            }
        }

        return await GetByIdAsync(runId, cancellationToken);
    }

    private static async Task<List<WorkflowRunRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HistoryPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(HistoryPath);
        return await JsonSerializer.DeserializeAsync<List<WorkflowRunRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private static async Task SaveAsync(List<WorkflowRunRecord> runs, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);

        await using var stream = File.Create(HistoryPath);
        await JsonSerializer.SerializeAsync(stream, runs, JsonOptions, cancellationToken);
    }

    private static async Task<WorkflowRunRecord?> UpdateStepAsync(
        string runId,
        string stepId,
        Action<WorkflowRunStepRecord> update,
        CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var runs = await LoadAsync(cancellationToken);
            var run = runs.FirstOrDefault(run => run.Id.Equals(runId, StringComparison.OrdinalIgnoreCase));
            var step = run?.Steps?.FirstOrDefault(step => step.StepId.Equals(stepId, StringComparison.OrdinalIgnoreCase));

            if (run is null || step is null)
            {
                return null;
            }

            update(step);
            run.Status = CalculateRunStatus(run.Steps);
            await SaveAsync(runs, cancellationToken);
            return Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static Task<WorkflowRunRecord?> UpdateStepPromptAsync(
        string runId,
        string stepId,
        string generatedPrompt,
        CancellationToken cancellationToken)
    {
        return UpdateStepAsync(runId, stepId, step =>
        {
            step.GeneratedTaskPrompt = generatedPrompt;
        }, cancellationToken);
    }

    private static async Task TryUpdateStepAsync(
        string runId,
        string stepId,
        Action<WorkflowRunStepRecord> update)
    {
        try
        {
            await UpdateStepAsync(runId, stepId, update, CancellationToken.None);
        }
        catch
        {
            // Preserve the original execution failure or cancellation for the caller.
        }
    }

    private static void ValidateExecutableStep(WorkflowRunStepRecord step)
    {
        if (string.IsNullOrWhiteSpace(step.Model))
        {
            throw new ArgumentException($"Workflow step '{step.StepName}' does not specify a model.", nameof(step));
        }

        if (string.IsNullOrWhiteSpace(step.GeneratedTaskPrompt))
        {
            throw new ArgumentException($"Workflow step '{step.StepName}' does not contain a generated prompt.", nameof(step));
        }
    }

    private static void ApplyStatus(WorkflowRunStepRecord step, WorkflowRunStepStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        step.Status = status;

        switch (status)
        {
            case WorkflowRunStepStatus.Planned:
                step.StartedAt = null;
                step.CompletedAt = null;
                break;
            case WorkflowRunStepStatus.Running:
                step.StartedAt ??= now;
                step.CompletedAt = null;
                break;
            case WorkflowRunStepStatus.Completed:
            case WorkflowRunStepStatus.Failed:
            case WorkflowRunStepStatus.Skipped:
                step.StartedAt ??= now;
                step.CompletedAt = now;
                break;
        }
    }

    private static WorkflowRunStatus CalculateRunStatus(IReadOnlyCollection<WorkflowRunStepRecord> steps)
    {
        var enabledSteps = steps.Where(step => step.Enabled).ToArray();

        if (enabledSteps.Any(step => step.Status == WorkflowRunStepStatus.Failed))
        {
            return WorkflowRunStatus.Failed;
        }

        if (enabledSteps.Length > 0 && enabledSteps.All(step => step.Status is WorkflowRunStepStatus.Completed or WorkflowRunStepStatus.Skipped))
        {
            return WorkflowRunStatus.Completed;
        }

        if (enabledSteps.Any(step => step.Status is WorkflowRunStepStatus.Running or WorkflowRunStepStatus.Completed))
        {
            return WorkflowRunStatus.Running;
        }

        return WorkflowRunStatus.Planned;
    }

    private static WorkflowRunRecord Clone(WorkflowRunRecord run)
    {
        return new WorkflowRunRecord
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowName = run.WorkflowName,
            GoalText = run.GoalText,
            ProjectName = run.ProjectName,
            Created = run.Created,
            Status = run.Status,
            Steps = (run.Steps ?? [])
                .OrderBy(step => step.Order)
                .Select(step => new WorkflowRunStepRecord
                {
                    StepId = step.StepId,
                    Order = step.Order,
                    Enabled = step.Enabled,
                    StepName = step.StepName,
                    AgentId = step.AgentId,
                    AgentName = step.AgentName,
                    Model = step.Model,
                    Instruction = step.Instruction,
                    GeneratedTaskPrompt = step.GeneratedTaskPrompt,
                    IncludePreviousResults = step.IncludePreviousResults,
                    PreviousResultMode = step.PreviousResultMode,
                    DependsOnStepIds = [.. (step.DependsOnStepIds ?? [])],
                    ResultText = step.ResultText ?? string.Empty,
                    StartedAt = step.StartedAt,
                    CompletedAt = step.CompletedAt,
                    Notes = step.Notes ?? string.Empty,
                    Status = step.Status
                })
                .ToList()
        };
    }
}
