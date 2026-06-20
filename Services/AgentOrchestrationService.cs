using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentOrchestrationService(
    IWebHostEnvironment environment,
    TaskDecompositionService taskDecompositionService,
    TaskSlicePatchPreviewPreparationService patchPreviewPreparationService,
    ICoderConsoleService coderConsoleService,
    IAgentModeProfileService agentModeProfileService,
    TaskSliceVerificationService taskSliceVerificationService,
    ILocalCoderPatchService localCoderPatchService,
    ILocalCoderReviewService localCoderReviewService,
    IPatchPackageService patchPackageService,
    TaskSliceApprovalService taskSliceApprovalService,
    TaskSliceApplyAuditService taskSliceApplyAuditService,
    TaskSliceApplyService taskSliceApplyService,
    HumanApprovalQueueService humanApprovalQueueService,
    AgentOrchestrationCheckpointService checkpointService,
    ExecutionPolicyProfileService executionPolicyProfileService,
    AgentOrchestrationSafetyService safetyService,
    AgentOrchestrationTimelineService timelineService,
    GitSyncService gitSyncService)
{
    private const string RunsFileName = "agent-orchestration-runs.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentOrchestrationRun>> GetLatestAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationRun?> GetByIdAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var run = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase));
            return run is null ? null : Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationRun> CreateRunAsync(
        string taskName,
        string userRequest,
        bool commitAndSync,
        bool approveHighRiskApply = false,
        CancellationToken cancellationToken = default)
    {
        var run = await CreateRunRecordAsync(taskName, userRequest, commitAndSync, [], AgentOrchestrationStatus.Pending, approveHighRiskApply, cancellationToken);
        return run;
    }

    public Task<ExecutionPolicyProfile?> GetExecutionPolicyProfileAsync(string profileName, CancellationToken cancellationToken = default)
    {
        return executionPolicyProfileService.GetByNameAsync(profileName, cancellationToken);
    }

    public async Task<AgentOrchestrationRun> PauseOrchestrationAsync(
        string runId,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        AgentOrchestrationRun run;
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            run = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Run not found: {runId}");
        }
        finally
        {
            FileLock.Release();
        }

        if (run.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.Failed)
        {
            throw new InvalidOperationException($"Run {run.Id} is {run.Status.ToString().ToLowerInvariant()} and cannot be paused.");
        }

        if (run.Status == AgentOrchestrationStatus.Paused)
        {
            return Clone(run);
        }

        if (run.Steps.Count == 0)
        {
            throw new InvalidOperationException("Run does not have any steps to pause.");
        }

        if (run.Steps.Any(step => step.Status == AgentOrchestrationStatus.InProgress))
        {
            throw new InvalidOperationException("Pause is only supported between steps.");
        }

        var currentStepIndex = run.Steps.FindLastIndex(step => step.Status == AgentOrchestrationStatus.Completed);
        var nextStepIndex = run.Steps.FindIndex(Math.Max(currentStepIndex + 1, 0), step => step.Status == AgentOrchestrationStatus.Pending);

        if (nextStepIndex < 0)
        {
            throw new InvalidOperationException("Run does not have a pending step to pause.");
        }

        var currentStepName = currentStepIndex >= 0 ? run.Steps[currentStepIndex].StepName : string.Empty;
        var nextStep = run.Steps[nextStepIndex];
        var pauseMessage = string.IsNullOrWhiteSpace(message)
            ? $"Run paused before {nextStep.StepName}."
            : message.Trim();

        nextStep.Status = AgentOrchestrationStatus.Paused;
        nextStep.StartedAtUtc = nextStep.StartedAtUtc == default ? DateTime.UtcNow : nextStep.StartedAtUtc;
        nextStep.CompletedAtUtc = null;
        nextStep.DurationMs = 0;
        nextStep.Success = false;
        nextStep.ErrorMessage = string.Empty;
        nextStep.Message = pauseMessage;

        run.Status = AgentOrchestrationStatus.Paused;
        run.PausedAtUtc = DateTime.UtcNow;
        run.PausedReason = pauseMessage;
        run.HumanApprovalPending = false;
        run.CompletedAtUtc = null;
        run.NextStepIndex = nextStepIndex;

        await RecordPauseCheckpointAsync(run, currentStepName, nextStep.StepName, pauseMessage, cancellationToken);
        await PersistRunAsync(run, cancellationToken);
        return Clone(run);
    }

    public async Task<AgentOrchestrationRun> ResumeOrchestrationAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        AgentOrchestrationRun? run;
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            run = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Run not found: {runId}");
        }
        finally
        {
            FileLock.Release();
        }

        if (run.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.Failed)
        {
            throw new InvalidOperationException($"Run {run.Id} is {run.Status.ToString().ToLowerInvariant()} and cannot be resumed.");
        }

        if (run.Status != AgentOrchestrationStatus.Paused)
        {
            return Clone(run);
        }

        var checkpoint = await checkpointService.GetLatestForRunAsync(run.Id, cancellationToken);

        var pausedStepIndex = run.Steps.FindIndex(step => step.Status == AgentOrchestrationStatus.Paused);
        if (pausedStepIndex < 0 && checkpoint is not null && !string.IsNullOrWhiteSpace(checkpoint.NextStepName))
        {
            pausedStepIndex = run.Steps.FindIndex(step => step.StepName.Equals(checkpoint.NextStepName, StringComparison.OrdinalIgnoreCase));
        }

        if (pausedStepIndex < 0 && run.NextStepIndex >= 0 && run.NextStepIndex < run.Steps.Count)
        {
            pausedStepIndex = run.NextStepIndex;
        }

        if (pausedStepIndex < 0)
        {
            pausedStepIndex = run.Steps.FindIndex(step => step.Status == AgentOrchestrationStatus.Pending);
        }

        if (pausedStepIndex < 0)
        {
            throw new InvalidOperationException($"Run {run.Id} does not have a pending step to resume.");
        }

        var pausedStep = run.Steps[pausedStepIndex];

        run.Status = AgentOrchestrationStatus.InProgress;
        run.HumanApprovalPending = false;
        run.PausedAtUtc = null;
        run.PausedReason = string.Empty;
        run.CompletedAtUtc = null;
        await PersistRunAsync(run, cancellationToken);

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.RunResumed,
            StepName = pausedStep.StepName,
            Message = string.IsNullOrWhiteSpace(checkpoint?.Message)
                ? $"Run resumed from {pausedStep.StepName}."
                : $"Run resumed from {pausedStep.StepName}: {checkpoint.Message}",
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = checkpoint?.Id ?? run.Id
        }, cancellationToken);

        if (!pausedStep.StepName.Equals("Apply", StringComparison.OrdinalIgnoreCase))
        {
            pausedStep.Status = AgentOrchestrationStatus.InProgress;
            pausedStep.StartedAtUtc = pausedStep.StartedAtUtc == default ? DateTime.UtcNow : pausedStep.StartedAtUtc;
            pausedStep.CompletedAtUtc = null;
            pausedStep.DurationMs = 0;
            pausedStep.Success = false;
            pausedStep.ErrorMessage = string.Empty;
            pausedStep.Message = string.IsNullOrWhiteSpace(checkpoint?.Message)
                ? $"Resumed {pausedStep.StepName}."
                : checkpoint.Message;
            run.NextStepIndex = pausedStepIndex;
            await PersistRunAsync(run, cancellationToken);
            return Clone(run);
        }

        var approvalRequest = !string.IsNullOrWhiteSpace(run.HumanApprovalRequestId)
            ? await humanApprovalQueueService.GetByIdAsync(run.HumanApprovalRequestId, cancellationToken)
            : await humanApprovalQueueService.GetByRunIdAsync(run.Id, cancellationToken);

        if (approvalRequest is null)
        {
            throw new InvalidOperationException($"Human approval request not found for run {run.Id}.");
        }

        if (approvalRequest.Status != HumanApprovalRequestStatus.Approved)
        {
            throw new InvalidOperationException($"Human approval request for run {run.Id} has not been approved.");
        }

        var slice = BuildCheckpointSlice(run);
        var package = await patchPackageService.GetByIdAsync(run.CheckpointPatchPackageId, cancellationToken)
            ?? throw new InvalidOperationException($"Patch package not found: {run.CheckpointPatchPackageId}");
        var applyStepIndex = pausedStepIndex;

        try
        {
            var applyStep = run.Steps.ElementAtOrDefault(applyStepIndex)
                ?? throw new InvalidOperationException("Apply step is missing from the orchestration run.");
            var gitStep = run.Steps.ElementAtOrDefault(applyStepIndex + 1)
                ?? throw new InvalidOperationException("Git Sync step is missing from the orchestration run.");

            var paused = await HandleApplyStepAsync(
                run,
                applyStep,
                slice,
                package,
                approvalGranted: true,
                cancellationToken);

            if (paused)
            {
                return Clone(run);
            }

            if (run.CommitAndSync)
            {
                await HandleGitSyncStepAsync(run, gitStep, cancellationToken);
            }
            else
            {
                await MarkStepSkippedAsync(run, gitStep, "Git sync skipped because CommitAndSync is false.", cancellationToken);
                run.Status = AgentOrchestrationStatus.Completed;
                run.CompletedAtUtc = DateTime.UtcNow;
                await PersistRunAsync(run, cancellationToken);
            }

            return Clone(run);
        }
        catch (Exception exception)
        {
            await SkipRemainingStepsAsync(run, applyStepIndex + 1, $"Skipped because orchestration failed after approval resume: {exception.Message}", cancellationToken);
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await PersistRunAsync(run, cancellationToken);
            return Clone(run);
        }
    }

    public async Task<AgentOrchestrationRun> RejectHumanApprovalAsync(
        string runId,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        AgentOrchestrationRun run;
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            run = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Run not found: {runId}");
        }
        finally
        {
            FileLock.Release();
        }

        if (run.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.Failed)
        {
            throw new InvalidOperationException($"Run {run.Id} is {run.Status.ToString().ToLowerInvariant()} and cannot be resumed.");
        }

        if (run.Status != AgentOrchestrationStatus.Paused)
        {
            return Clone(run);
        }

        var applyStepIndex = run.NextStepIndex > 0 ? run.NextStepIndex : 4;
        var applyStep = run.Steps.ElementAtOrDefault(applyStepIndex)
                        ?? throw new InvalidOperationException("Apply step is missing from the orchestration run.");

        var rejectionMessage = string.IsNullOrWhiteSpace(notes)
            ? "Human approval was rejected."
            : notes.Trim();

        run.HumanApprovalPending = false;
        run.PausedAtUtc = null;
        run.PausedReason = rejectionMessage;
        run.ApplySucceeded = false;
        run.ApplyMessage = rejectionMessage;
        run.ApplyRiskGateMessage = rejectionMessage;

        await MarkStepFailedAsync(run, applyStep, rejectionMessage, cancellationToken);
        await SkipRemainingStepsAsync(run, applyStepIndex + 1, "Skipped because human approval was rejected.", cancellationToken);

        run.Status = AgentOrchestrationStatus.Failed;
        run.CompletedAtUtc = DateTime.UtcNow;
        await PersistRunAsync(run, cancellationToken);
        return Clone(run);
    }

    public async Task<AgentOrchestrationRun> RetryFailedStepAsync(
        AgentOrchestrationRetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Run id is required.", nameof(request));
        }

        AgentOrchestrationRun run;
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            run = (await LoadAsync(cancellationToken))
                .FirstOrDefault(item => item.Id.Equals(request.RunId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Run not found: {request.RunId}");
        }
        finally
        {
            FileLock.Release();
        }

        if (run.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.InProgress)
        {
            throw new InvalidOperationException($"Run {run.Id} is {run.Status.ToString().ToLowerInvariant()} and cannot be retried.");
        }

        if (run.Status != AgentOrchestrationStatus.Failed)
        {
            throw new InvalidOperationException($"Run {run.Id} is {run.Status.ToString().ToLowerInvariant()} and cannot be retried.");
        }

        var failedStepIndex = run.Steps.FindIndex(step => step.Status == AgentOrchestrationStatus.Failed);
        if (failedStepIndex < 0)
        {
            throw new InvalidOperationException($"Run {run.Id} does not have a failed step to retry.");
        }

        var failedStep = run.Steps[failedStepIndex];
        await RecordRetryRequestedAsync(run, failedStep, request, cancellationToken);

        try
        {
            if (failedStep.StepName.Equals("Apply", StringComparison.OrdinalIgnoreCase))
            {
                await RetryApplyStepAsync(run, failedStepIndex, cancellationToken);
            }
            else if (failedStep.StepName.Equals("Git Sync", StringComparison.OrdinalIgnoreCase))
            {
                await HandleGitSyncStepAsync(run, failedStep, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException($"Retry is not supported for failed step '{failedStep.StepName}'.");
            }

            await RecordRetryCompletedAsync(run, failedStep, request, cancellationToken);
            return Clone(run);
        }
        catch (Exception exception)
        {
            await RecordRetryFailedAsync(run, failedStep, request, exception.Message, cancellationToken);
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await PersistRunAsync(run, cancellationToken);
            return Clone(run);
        }
    }

    public async Task<AgentOrchestrationRun> RunOrchestrationAsync(
        string taskName,
        string userRequest,
        bool commitAndSync,
        bool approveHighRiskApply = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name is required.", nameof(taskName));
        }

        var steps = CreateDefaultSteps();
        var run = await CreateRunRecordAsync(taskName, userRequest, commitAndSync, steps, AgentOrchestrationStatus.InProgress, approveHighRiskApply, cancellationToken);

        TaskPlan? plan = null;
        TaskPlanSlice? slice = null;
        LocalCoderPatchPreview? preview = null;
        PatchPackage? package = null;
        TaskSliceExecutionResult? verificationResult = null;
        LocalCoderResult? reviewResult = null;

        for (var index = 0; index < run.Steps.Count; index++)
        {
            var step = run.Steps[index];

            try
            {
                if (step.StepName == "Planner" ||
                    step.StepName == "Patch Builder" ||
                    step.StepName == "Verifier" ||
                    step.StepName == "Reviewer")
                {
                    await ExecuteStepAsync(
                        run,
                        step,
                        async ct =>
                        {
                            switch (step.StepName)
                            {
                                case "Planner":
                                    plan = taskDecompositionService.BuildPlan(userRequest);
                                    slice = plan.Slices.FirstOrDefault() ?? throw new InvalidOperationException("Planner did not produce a slice.");
                                    EnrichSliceFromRequest(slice, userRequest);
                                    return new StepOutcome(
                                        $"Planner generated {plan.Slices.Count} slice(s).",
                                        $"Plan {plan.Id} generated with {plan.Slices.Count} slice(s).");

                                case "Patch Builder":
                                    EnsureSliceReady(slice);
                                    preview = await BuildPatchPreviewAsync(slice!, userRequest, ct);
                                    package = await patchPackageService.CreateFromPreviewAsync(preview, userRequest, ct);
                                    slice!.PatchPackageId = package.Id;
                                    return new StepOutcome(
                                        $"Patch preview generated with {preview.FileChanges.Count} change(s).",
                                        $"Patch package {package.Id} created.");

                                case "Verifier":
                                    EnsureSliceReady(slice);
                                    slice!.Status = TaskSliceStatus.Previewed;
                                    slice.UpdatedAt = DateTime.UtcNow;
                                    verificationResult = await taskSliceVerificationService.VerifySliceAsync(slice, ct);
                                    if (!verificationResult.Success)
                                    {
                                        throw new InvalidOperationException(verificationResult.Summary);
                                    }

                                    return new StepOutcome(
                                        verificationResult.Summary,
                                        verificationResult.Summary);

                                case "Reviewer":
                                    EnsureSliceReady(slice);
                                    EnsurePreviewReady(preview);
                                    reviewResult = await RunReviewAsync(slice!, preview!, verificationResult, ct);
                                    if (!reviewResult.Success)
                                    {
                                        throw new InvalidOperationException(reviewResult.ErrorMessage);
                                    }

                                    return new StepOutcome(
                                        reviewResult.Output,
                                        "Local review completed successfully.");

                                default:
                                    throw new InvalidOperationException($"Unknown orchestration step: {step.StepName}");
                            }
                        },
                        cancellationToken);
                    continue;
                }

                if (step.StepName == "Apply")
                {
                    EnsureSliceReady(slice);
                    EnsurePreviewReady(preview);
                    EnsurePackageReady(package);
                    var paused = await HandleApplyStepAsync(run, step, slice!, package!, run.ApproveHighRiskApply, cancellationToken);
                    if (paused)
                    {
                        return Clone(run);
                    }

                    continue;
                }

                if (step.StepName == "Git Sync")
                {
                    await HandleGitSyncStepAsync(run, step, cancellationToken);
                    continue;
                }

                throw new InvalidOperationException($"Unknown orchestration step: {step.StepName}");
            }
            catch (Exception exception)
            {
                if (step.StepName != "Apply" && step.StepName != "Git Sync")
                {
                    await MarkStepFailedAsync(run, step, exception.Message, cancellationToken);
                }
                await SkipRemainingStepsAsync(run, index + 1, $"Skipped because orchestration failed at {step.StepName}.", cancellationToken);
                run.Status = AgentOrchestrationStatus.Failed;
                run.CompletedAtUtc = DateTime.UtcNow;
                await PersistRunAsync(run, cancellationToken);
                return Clone(run);
            }
        }

        run.Status = AgentOrchestrationStatus.Completed;
        run.CompletedAtUtc = DateTime.UtcNow;
        await PersistRunAsync(run, cancellationToken);
        return Clone(run);
    }

    public async Task<AgentOrchestrationRun> AddStepAsync(
        string runId,
        string stepName,
        AgentMode agentRole,
        string? relatedRunId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(stepName))
        {
            throw new ArgumentException("Step name is required.", nameof(stepName));
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var runs = await LoadAsync(cancellationToken);
            var run = runs.FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Run not found: {runId}");

            var step = new AgentOrchestrationStep
            {
                Id = Guid.NewGuid().ToString("N"),
                StepName = stepName.Trim(),
                AgentRole = agentRole,
                Status = AgentOrchestrationStatus.Pending,
                RelatedRunId = string.IsNullOrWhiteSpace(relatedRunId) ? run.Id : relatedRunId.Trim()
            };

            run.Steps.Add(step);
            UpdateRunStatus(run);
            await SaveAsync(runs, cancellationToken);
            return Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public Task<AgentOrchestrationRun> StartStepAsync(
        string runId,
        string stepId,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        return UpdateManualStepAsync(runId, stepId, step =>
        {
            step.Status = AgentOrchestrationStatus.InProgress;
            step.StartedAtUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc;
            step.CompletedAtUtc = null;
            step.DurationMs = 0;
            step.Success = false;
            step.ErrorMessage = string.Empty;
            step.Message = string.IsNullOrWhiteSpace(message) ? $"Started {step.StepName}." : message.Trim();
        }, cancellationToken);
    }

    public Task<AgentOrchestrationRun> CompleteStepAsync(
        string runId,
        string stepId,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        return UpdateManualStepAsync(runId, stepId, step =>
        {
            step.Status = AgentOrchestrationStatus.Completed;
            step.StartedAtUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = true;
            step.ErrorMessage = string.Empty;
            step.Message = string.IsNullOrWhiteSpace(message) ? $"Completed {step.StepName}." : message.Trim();
        }, cancellationToken);
    }

    public Task<AgentOrchestrationRun> FailStepAsync(
        string runId,
        string stepId,
        string message,
        CancellationToken cancellationToken = default)
    {
        return UpdateManualStepAsync(runId, stepId, step =>
        {
            step.Status = AgentOrchestrationStatus.Failed;
            step.StartedAtUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = false;
            step.ErrorMessage = message.Trim();
            step.Message = message.Trim();
        }, cancellationToken);
    }

    private async Task ExecuteStepAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        Func<CancellationToken, Task<StepOutcome>> executor,
        CancellationToken cancellationToken)
    {
        step.Status = AgentOrchestrationStatus.InProgress;
        step.StartedAtUtc = DateTime.UtcNow;
        step.CompletedAtUtc = null;
        step.DurationMs = 0;
        step.Success = false;
        step.ErrorMessage = string.Empty;
        step.Message = $"Running {step.StepName}.";
        run.Status = AgentOrchestrationStatus.InProgress;
        run.CompletedAtUtc = null;
        await PersistRunAsync(run, cancellationToken);
        await RecordStepStartedAsync(run, step, cancellationToken);

        try
        {
            var outcome = await executor(cancellationToken);
            step.Status = AgentOrchestrationStatus.Completed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = true;
            step.Message = string.Join(" ", new[] { outcome.Message, outcome.Details }.Where(text => !string.IsNullOrWhiteSpace(text)));
            step.ErrorMessage = string.Empty;
            await RecordStepCompletedAsync(run, step, step.Message, cancellationToken);
        }
        catch (Exception exception)
        {
            step.Status = AgentOrchestrationStatus.Failed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = false;
            step.ErrorMessage = exception.Message;
            step.Message = exception.Message;
            throw;
        }
        finally
        {
            await PersistRunAsync(run, cancellationToken);
        }
    }

    private async Task MarkStepFailedAsync(AgentOrchestrationRun run, AgentOrchestrationStep step, string errorMessage, CancellationToken cancellationToken)
    {
        step.Status = AgentOrchestrationStatus.Failed;
        step.CompletedAtUtc ??= DateTime.UtcNow;
        step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
        step.Success = false;
        step.ErrorMessage = errorMessage;
        step.Message = errorMessage;
        run.Status = AgentOrchestrationStatus.Failed;
        run.CompletedAtUtc = DateTime.UtcNow;
        await RecordStepFailedAsync(run, step, errorMessage, cancellationToken);
        await PersistRunAsync(run, cancellationToken);
    }

    private async Task<AgentOrchestrationRun> UpdateManualStepAsync(
        string runId,
        string stepId,
        Action<AgentOrchestrationStep> update,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(stepId))
        {
            throw new ArgumentException("Step id is required.", nameof(stepId));
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var runs = await LoadAsync(cancellationToken);
            var run = runs.FirstOrDefault(item => item.Id.Equals(runId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Run not found: {runId}");

            var step = run.Steps.FirstOrDefault(item => item.Id.Equals(stepId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Step not found: {stepId}");

            update(step);
            await RecordManualStepEventAsync(run, step, cancellationToken);
            UpdateRunStatus(run);
            await SaveAsync(runs, cancellationToken);
            return Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task RecordManualStepEventAsync(AgentOrchestrationRun run, AgentOrchestrationStep step, CancellationToken cancellationToken)
    {
        switch (step.Status)
        {
            case AgentOrchestrationStatus.InProgress:
                await RecordStepStartedAsync(run, step, cancellationToken);
                break;
            case AgentOrchestrationStatus.Completed:
                await RecordStepCompletedAsync(run, step, step.Message, cancellationToken);
                break;
            case AgentOrchestrationStatus.Failed:
                await RecordStepFailedAsync(run, step, step.ErrorMessage, cancellationToken);
                break;
        }
    }

    private async Task SkipRemainingStepsAsync(
        AgentOrchestrationRun run,
        int startIndex,
        string reason,
        CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i];
            if (step.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.Failed)
            {
                continue;
            }

            step.Status = AgentOrchestrationStatus.Skipped;
            step.StartedAtUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = 0;
            step.Success = false;
            step.ErrorMessage = reason;
            step.Message = reason;

            if (step.StepName == "Git Sync")
            {
                run.GitMessage = reason;
                await RecordGitSyncSkippedAsync(run, reason, cancellationToken);
            }
        }

        await PersistRunAsync(run, cancellationToken);
    }

    private async Task<bool> HandleApplyStepAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        TaskPlanSlice slice,
        PatchPackage package,
        bool approvalGranted,
        CancellationToken cancellationToken)
    {
        step.Status = AgentOrchestrationStatus.InProgress;
        step.StartedAtUtc = DateTime.UtcNow;
        step.CompletedAtUtc = null;
        step.DurationMs = 0;
        step.Success = false;
        step.ErrorMessage = string.Empty;
        step.Message = $"Running {step.StepName}.";
        run.Status = AgentOrchestrationStatus.InProgress;
        run.CompletedAtUtc = null;
        await PersistRunAsync(run, cancellationToken);
        await RecordStepStartedAsync(run, step, cancellationToken);

        var changedFiles = GetChangedFiles(package);
        var safetyReport = await GetOrCreateSafetyReportAsync(run, slice, changedFiles, cancellationToken);
        run.ApproveHighRiskApply = run.ApproveHighRiskApply || approvalGranted;

        if (safetyReport.BlocksAutoApply)
        {
            run.ApplySucceeded = false;
            run.ApplyMessage = safetyReport.Summary;
            run.ApplyRiskGateMessage = safetyReport.Summary;
            step.Status = AgentOrchestrationStatus.Failed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = false;
            step.ErrorMessage = safetyReport.Summary;
            step.Message = safetyReport.Summary;
            await RecordStepFailedAsync(run, step, safetyReport.Summary, cancellationToken);
            await RecordApplySkippedAsync(run, slice, safetyReport, cancellationToken);
            throw new InvalidOperationException(safetyReport.Summary);
        }

        if (safetyReport.RequiresManualApproval && !approvalGranted)
        {
            var request = await GetOrCreateApprovalRequestAsync(run, safetyReport, cancellationToken);
            var applyStepIndex = Math.Max(run.Steps.FindIndex(item => item.Id.Equals(step.Id, StringComparison.OrdinalIgnoreCase)), 0);
            var nextStep = run.Steps.ElementAtOrDefault(applyStepIndex + 1);

            run.ApplySucceeded = false;
            run.ApplyMessage = $"Waiting for human approval: {safetyReport.Summary}";
            run.ApplyRiskGateMessage = safetyReport.Summary;
            run.HumanApprovalRequestId = request.Id;
            run.HumanApprovalPending = true;
            run.PausedAtUtc = DateTime.UtcNow;
            run.PausedReason = safetyReport.Summary;
            run.NextStepIndex = applyStepIndex;
            run.CheckpointPlanId = slice.PlanId;
            run.CheckpointSliceId = slice.Id;
            run.CheckpointSliceTitle = slice.Title;
            run.CheckpointPatchPackageId = package.Id;
            run.CheckpointRiskLevel = safetyReport.HighestRiskLevel;

            step.Status = AgentOrchestrationStatus.Paused;
            step.CompletedAtUtc = null;
            step.DurationMs = 0;
            step.Success = false;
            step.ErrorMessage = string.Empty;
            step.Message = run.ApplyMessage;
            run.Status = AgentOrchestrationStatus.Paused;
            run.CompletedAtUtc = null;
            await RecordPauseCheckpointAsync(run, step.StepName, nextStep?.StepName ?? string.Empty, safetyReport.Summary, cancellationToken);
            await PersistRunAsync(run, cancellationToken);
            return true;
        }

        try
        {
            await taskSliceApprovalService.ApproveAsync(slice, "Agent Orchestration", cancellationToken);
            await patchPackageService.ApproveAsync(package.Id, cancellationToken);

            var existingAuditIds = (await taskSliceApplyAuditService.GetLatestAsync(25, cancellationToken))
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            TaskSliceApplyResult applyResult;
            try
            {
                applyResult = await taskSliceApplyService.ApplyAsync(environment.ContentRootPath, slice, true, cancellationToken);
            }
            catch (Exception exception)
            {
                run.ApplySucceeded = false;
                run.ApplyMessage = exception.Message;
                run.ApplyRiskGateMessage = exception.Message;
                step.Status = AgentOrchestrationStatus.Failed;
                step.CompletedAtUtc = DateTime.UtcNow;
                step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
                step.Success = false;
                step.ErrorMessage = exception.Message;
                step.Message = exception.Message;
                run.Status = AgentOrchestrationStatus.Failed;
                run.CompletedAtUtc = DateTime.UtcNow;
                await RecordStepFailedAsync(run, step, exception.Message, cancellationToken);
                await PersistRunAsync(run, cancellationToken);
                throw;
            }

            var auditIds = await GetApplyAuditIdsAsync(slice.Id, existingAuditIds, cancellationToken);
            var appliedFiles = changedFiles;

            run.ApplySucceeded = applyResult.Success;
            run.ApplyMessage = BuildApplyMessage(applyResult);
            run.ApplyRiskGateMessage = applyResult.RiskGateMessage ?? string.Empty;
            run.ApplyAuditIds = auditIds.ToList();

            if (applyResult.Success)
            {
                run.AppliedSliceIds = [slice.Id];
                run.AppliedFiles = [.. appliedFiles];
                step.Status = AgentOrchestrationStatus.Completed;
                step.CompletedAtUtc = DateTime.UtcNow;
                step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
                step.Success = true;
                step.ErrorMessage = string.Empty;
                step.Message = string.Join(" ", new[] { applyResult.Message, applyResult.RiskGateMessage }.Where(text => !string.IsNullOrWhiteSpace(text)));
                await RecordApplyCompletedAsync(run, slice, appliedFiles, cancellationToken);
                await PersistRunAsync(run, cancellationToken);
                return false;
            }

            step.Status = AgentOrchestrationStatus.Failed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = false;
            step.ErrorMessage = string.IsNullOrWhiteSpace(applyResult.RiskGateMessage)
                ? applyResult.Message
                : applyResult.RiskGateMessage;
            step.Message = step.ErrorMessage;
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await RecordStepFailedAsync(run, step, step.ErrorMessage, cancellationToken);
            await RecordApplySkippedAsync(run, slice, safetyReport, cancellationToken);
            await PersistRunAsync(run, cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(applyResult.RiskGateMessage)
                ? applyResult.Message
                : applyResult.RiskGateMessage);
        }
        catch (Exception exception)
        {
            if (step.Status != AgentOrchestrationStatus.Failed && step.Status != AgentOrchestrationStatus.Paused)
            {
                step.Status = AgentOrchestrationStatus.Failed;
                step.CompletedAtUtc = DateTime.UtcNow;
                step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
                step.Success = false;
                step.ErrorMessage = exception.Message;
                step.Message = exception.Message;
                run.ApplySucceeded = false;
                run.ApplyMessage = exception.Message;
                run.ApplyRiskGateMessage = exception.Message;
                run.Status = AgentOrchestrationStatus.Failed;
                run.CompletedAtUtc = DateTime.UtcNow;
                await RecordStepFailedAsync(run, step, exception.Message, cancellationToken);
                await PersistRunAsync(run, cancellationToken);
            }

            throw;
        }
    }

    private async Task<GitSyncResult> HandleGitSyncStepAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        CancellationToken cancellationToken)
    {
        step.Status = AgentOrchestrationStatus.InProgress;
        step.StartedAtUtc = DateTime.UtcNow;
        step.CompletedAtUtc = null;
        step.DurationMs = 0;
        step.Success = false;
        step.ErrorMessage = string.Empty;
        step.Message = $"Running {step.StepName}.";
        run.Status = AgentOrchestrationStatus.InProgress;
        run.CompletedAtUtc = null;
        await PersistRunAsync(run, cancellationToken);
        await RecordStepStartedAsync(run, step, cancellationToken);

        if (!run.CommitAndSync)
        {
            await MarkStepSkippedAsync(run, step, "Git sync skipped because CommitAndSync is false.", cancellationToken);
            run.Status = AgentOrchestrationStatus.Completed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await PersistRunAsync(run, cancellationToken);
            return new GitSyncResult
            {
                CommitAttempted = false,
                CommitSucceeded = false,
                PushAttempted = false,
                PushSucceeded = false,
                GitMessage = "Git sync skipped because CommitAndSync is false."
            };
        }

        GitSyncResult gitSyncResult;
        try
        {
            gitSyncResult = await gitSyncService.SyncAsync(environment.ContentRootPath, run.TaskName, cancellationToken);
        }
        catch (Exception exception)
        {
            run.GitMessage = exception.Message;
            await RecordGitSyncFailedAsync(run, exception.Message, run.CommitHash, cancellationToken);
            step.Status = AgentOrchestrationStatus.Failed;
            step.CompletedAtUtc = DateTime.UtcNow;
            step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
            step.Success = false;
            step.ErrorMessage = exception.Message;
            step.Message = exception.Message;
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await RecordStepFailedAsync(run, step, exception.Message, cancellationToken);
            await PersistRunAsync(run, cancellationToken);
            throw;
        }

        run.CommitAttempted = gitSyncResult.CommitAttempted;
        run.CommitSucceeded = gitSyncResult.CommitSucceeded;
        run.CommitHash = gitSyncResult.CommitHash;
        run.PushAttempted = gitSyncResult.PushAttempted;
        run.PushSucceeded = gitSyncResult.PushSucceeded;
        run.GitMessage = gitSyncResult.GitMessage;

        step.CompletedAtUtc = DateTime.UtcNow;
        step.DurationMs = CalculateDuration(step.StartedAtUtc, step.CompletedAtUtc.Value);
        step.Success = gitSyncResult.CommitSucceeded && gitSyncResult.PushSucceeded;
        step.Status = step.Success ? AgentOrchestrationStatus.Completed : AgentOrchestrationStatus.Failed;
        step.ErrorMessage = step.Success ? string.Empty : gitSyncResult.GitMessage;
        step.Message = gitSyncResult.GitMessage;

        if (!gitSyncResult.CommitSucceeded || !gitSyncResult.PushSucceeded)
        {
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc = DateTime.UtcNow;
            await RecordGitSyncFailedAsync(run, gitSyncResult.GitMessage, gitSyncResult.CommitHash, cancellationToken);
            await RecordStepFailedAsync(run, step, gitSyncResult.GitMessage, cancellationToken);
            await PersistRunAsync(run, cancellationToken);
            throw new InvalidOperationException(gitSyncResult.GitMessage);
        }

        run.Status = AgentOrchestrationStatus.Completed;
        run.CompletedAtUtc = DateTime.UtcNow;
        await RecordGitSyncCompletedAsync(run, gitSyncResult.GitMessage, gitSyncResult.CommitHash, cancellationToken);
        await PersistRunAsync(run, cancellationToken);
        return gitSyncResult;
    }

    private async Task RetryApplyStepAsync(
        AgentOrchestrationRun run,
        int failedStepIndex,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(run.CheckpointSliceId) || string.IsNullOrWhiteSpace(run.CheckpointPatchPackageId))
        {
            throw new InvalidOperationException($"Run {run.Id} does not have a persisted apply checkpoint to retry.");
        }

        var applyStep = run.Steps[failedStepIndex];
        var slice = BuildCheckpointSlice(run);
        var package = await patchPackageService.GetByIdAsync(run.CheckpointPatchPackageId, cancellationToken)
            ?? throw new InvalidOperationException($"Patch package not found: {run.CheckpointPatchPackageId}");

        var paused = await HandleApplyStepAsync(
            run,
            applyStep,
            slice,
            package,
            approvalGranted: run.ApproveHighRiskApply,
            cancellationToken);

        if (paused)
        {
            throw new InvalidOperationException("Retry paused waiting for human approval.");
        }

        if (run.CommitAndSync)
        {
            var gitStep = run.Steps.ElementAtOrDefault(failedStepIndex + 1)
                ?? throw new InvalidOperationException("Git Sync step is missing from the orchestration run.");

            await HandleGitSyncStepAsync(run, gitStep, cancellationToken);
            return;
        }

        var nextStep = run.Steps.ElementAtOrDefault(failedStepIndex + 1);
        if (nextStep is not null && nextStep.StepName.Equals("Git Sync", StringComparison.OrdinalIgnoreCase))
        {
            await MarkStepSkippedAsync(run, nextStep, "Git sync skipped because CommitAndSync is false.", cancellationToken);
        }

        run.Status = AgentOrchestrationStatus.Completed;
        run.CompletedAtUtc = DateTime.UtcNow;
        await PersistRunAsync(run, cancellationToken);
    }

    private async Task<HumanApprovalRequest> GetOrCreateApprovalRequestAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationSafetyReport safetyReport,
        CancellationToken cancellationToken)
    {
        var existing = await humanApprovalQueueService.GetByRunIdAsync(run.Id, cancellationToken);
        if (existing is not null && existing.Status == HumanApprovalRequestStatus.Pending)
        {
            return existing;
        }

        return await humanApprovalQueueService.CreateAsync(
            run.Id,
            run.TaskName,
            safetyReport.HighestRiskLevel,
            safetyReport.Summary,
            "Agent Orchestration",
            cancellationToken);
    }

    private async Task<AgentOrchestrationSafetyReport> GetOrCreateSafetyReportAsync(
        AgentOrchestrationRun run,
        TaskPlanSlice slice,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(run.SafetyReportId))
        {
            return BuildSafetyReportFromRun(run);
        }

        var safetyReport = await safetyService.GenerateAsync(run.Id, run.TaskName, slice, changedFiles, run.ExecutionPolicyName, cancellationToken);
        run.SafetyReportId = safetyReport.Id;
        run.SafetyReportCreatedAtUtc = safetyReport.CreatedAtUtc;
        run.SafetyHighestRiskLevel = safetyReport.HighestRiskLevel;
        run.SafetyTotalChangedFiles = safetyReport.TotalChangedFiles;
        run.SafetyRequiresManualApproval = safetyReport.RequiresManualApproval;
        run.SafetyBlocksAutoApply = safetyReport.BlocksAutoApply;
        run.SafetyReasons = [.. safetyReport.Reasons];
        run.SafetySummary = safetyReport.Summary;
        await RecordSafetyReviewGeneratedAsync(run, slice, safetyReport, cancellationToken);
        await PersistRunAsync(run, cancellationToken);
        return safetyReport;
    }

    private static AgentOrchestrationSafetyReport BuildSafetyReportFromRun(AgentOrchestrationRun run)
    {
        return new AgentOrchestrationSafetyReport
        {
            Id = run.SafetyReportId,
            RunId = run.Id,
            TaskName = run.TaskName,
            HighestRiskLevel = run.SafetyHighestRiskLevel,
            TotalChangedFiles = run.SafetyTotalChangedFiles,
            RequiresManualApproval = run.SafetyRequiresManualApproval,
            BlocksAutoApply = run.SafetyBlocksAutoApply,
            Reasons = [.. run.SafetyReasons],
            CreatedAtUtc = run.SafetyReportCreatedAtUtc ?? run.CreatedAtUtc,
            Summary = run.SafetySummary
        };
    }

    private static IReadOnlyList<string> GetChangedFiles(PatchPackage package)
    {
        return (package.FileChanges ?? [])
            .Select(change => change.RelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TaskPlanSlice BuildCheckpointSlice(AgentOrchestrationRun run)
    {
        return new TaskPlanSlice
        {
            Id = run.CheckpointSliceId,
            PlanId = run.CheckpointPlanId,
            Title = run.CheckpointSliceTitle,
            Goal = run.CheckpointSliceTitle,
            Description = run.PausedReason,
            RiskLevel = run.CheckpointRiskLevel,
            Status = TaskSliceStatus.Verified,
            PatchPackageId = run.CheckpointPatchPackageId
        };
    }

    private async Task MarkStepSkippedAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        string message,
        CancellationToken cancellationToken)
    {
        step.Status = AgentOrchestrationStatus.Skipped;
        step.StartedAtUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc;
        step.CompletedAtUtc = DateTime.UtcNow;
        step.DurationMs = 0;
        step.Success = false;
        step.ErrorMessage = message;
        step.Message = message;
        run.GitMessage = message;
        if (step.StepName == "Git Sync")
        {
            await RecordGitSyncSkippedAsync(run, message, cancellationToken);
        }
        await PersistRunAsync(run, cancellationToken);
    }

    private static void UpdateRunStatus(AgentOrchestrationRun run)
    {
        if (run.Steps.Count == 0)
        {
            run.Status = AgentOrchestrationStatus.Pending;
            run.CompletedAtUtc = null;
            return;
        }

        if (run.Steps.Any(step => step.Status == AgentOrchestrationStatus.Failed))
        {
            run.Status = AgentOrchestrationStatus.Failed;
            run.CompletedAtUtc ??= DateTime.UtcNow;
            return;
        }

        if (run.Steps.Any(step => step.Status == AgentOrchestrationStatus.Paused))
        {
            run.Status = AgentOrchestrationStatus.Paused;
            run.CompletedAtUtc = null;
            return;
        }

        if (run.Steps.All(step => step.Status is AgentOrchestrationStatus.Completed or AgentOrchestrationStatus.Skipped))
        {
            run.Status = AgentOrchestrationStatus.Completed;
            run.CompletedAtUtc ??= DateTime.UtcNow;
            return;
        }

        if (run.Steps.Any(step => step.Status == AgentOrchestrationStatus.InProgress))
        {
            run.Status = AgentOrchestrationStatus.InProgress;
            run.CompletedAtUtc = null;
            return;
        }

        run.Status = AgentOrchestrationStatus.Pending;
        run.CompletedAtUtc = null;
    }

    private async Task<LocalCoderPatchPreview> BuildPatchPreviewAsync(
        TaskPlanSlice slice,
        string userRequest,
        CancellationToken cancellationToken)
    {
        var preparation = await patchPreviewPreparationService.PrepareAsync(slice, environment.ContentRootPath, cancellationToken: cancellationToken);
        if (!preparation.CanGenerate)
        {
            throw new InvalidOperationException(preparation.FailureMessage);
        }

        var request = new LocalCoderRequest
        {
            ProjectPath = environment.ContentRootPath,
            Model = (await agentModeProfileService.GetByModeAsync(AgentMode.PatchBuilder, cancellationToken))?.Model ?? "qwen2.5-coder:7b",
            Task = userRequest,
            FileContexts = preparation.FileContexts.ToList(),
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            AllowedPatchFolders = [],
            AllowedCreateFolders = PatchIntentService.BuildIntent(new LocalCoderRequest
            {
                ProjectPath = environment.ContentRootPath,
                Model = "qwen2.5-coder:7b",
                Task = userRequest,
                FileContexts = preparation.FileContexts.ToList(),
                AllowedPatchScope = PatchScopeMode.ContextFilesOnly
            }).AllowedCreateFolders.ToList()
        };

        return await coderConsoleService.GeneratePatchPreviewAsync(request);
    }

    private async Task<LocalCoderResult> RunReviewAsync(
        TaskPlanSlice slice,
        LocalCoderPatchPreview preview,
        TaskSliceExecutionResult? verificationResult,
        CancellationToken cancellationToken)
    {
        var selectedPaths = slice.TargetFiles.Count > 0
            ? slice.TargetFiles.ToArray()
            : preview.FileContexts.Select(context => context.RelativePath).ToArray();
        var allowedPathsText = string.Join(Environment.NewLine, selectedPaths);
        var task = new AiBox.DevPortal.Models.Agents.LocalCoderTask
        {
            HistoryId = slice.Id,
            Title = slice.Title,
            RepositoryPath = environment.ContentRootPath,
            Model = (await agentModeProfileService.GetByModeAsync(AgentMode.Reviewer, cancellationToken))?.Model ?? "qwen2.5-coder:7b",
            Instructions = preview.Task,
            SelectedFilePaths = [.. selectedPaths],
            AllowedPathsText = allowedPathsText,
            ForbiddenPathsText = string.Empty,
            BuildCommand = "dotnet build",
            Status = LocalCoderTaskStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        var validation = localCoderPatchService.ValidatePatch(task, preview.PatchText);
        var buildOutput = verificationResult?.Summary ?? string.Empty;
        return await localCoderReviewService.ReviewAsync(task, preview.Task, preview.PatchText, validation, buildOutput, cancellationToken);
    }

    private async Task<AgentOrchestrationRun> CreateRunRecordAsync(
        string taskName,
        string userRequest,
        bool commitAndSync,
        IReadOnlyList<AgentOrchestrationStep> steps,
        AgentOrchestrationStatus status,
        bool approveHighRiskApply,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name is required.", nameof(taskName));
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var runs = await LoadAsync(cancellationToken);
            var run = new AgentOrchestrationRun
            {
                TaskName = taskName.Trim(),
                UserRequest = userRequest?.Trim() ?? string.Empty,
                CommitAndSync = commitAndSync,
                ApproveHighRiskApply = approveHighRiskApply,
                ExecutionPolicyName = "Safe",
                Status = status,
                CreatedAtUtc = DateTime.UtcNow,
                CompletedAtUtc = status == AgentOrchestrationStatus.Completed ? DateTime.UtcNow : null,
                ApplySucceeded = false,
                ApplyMessage = string.Empty,
                ApplyRiskGateMessage = string.Empty,
                AppliedSliceIds = [],
                AppliedFiles = [],
                ApplyAuditIds = [],
                CommitAttempted = false,
                CommitSucceeded = false,
                CommitHash = string.Empty,
                PushAttempted = false,
                PushSucceeded = false,
                GitMessage = string.Empty,
                HumanApprovalRequestId = string.Empty,
                HumanApprovalPending = false,
                PausedAtUtc = null,
                PausedReason = string.Empty,
                NextStepIndex = 0,
                CheckpointPlanId = string.Empty,
                CheckpointSliceId = string.Empty,
                CheckpointSliceTitle = string.Empty,
                CheckpointPatchPackageId = string.Empty,
                CheckpointRiskLevel = RiskLevel.Low,
                SafetyReportId = string.Empty,
                SafetyReportCreatedAtUtc = null,
                SafetyHighestRiskLevel = RiskLevel.Low,
                SafetyTotalChangedFiles = 0,
                SafetyRequiresManualApproval = false,
                SafetyBlocksAutoApply = false,
                SafetyReasons = [],
                SafetySummary = string.Empty,
                Steps = steps.Select(Clone).ToList()
            };

            foreach (var step in run.Steps)
            {
                step.RelatedRunId = run.Id;
            }

            runs.Add(run);
            await SaveAsync(runs, cancellationToken);
            await RecordRunCreatedAsync(run, cancellationToken);
            return Clone(run);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static IReadOnlyList<AgentOrchestrationStep> CreateDefaultSteps()
    {
        return
        [
            new AgentOrchestrationStep { StepName = "Planner", AgentRole = AgentMode.Planner, Status = AgentOrchestrationStatus.Pending },
            new AgentOrchestrationStep { StepName = "Patch Builder", AgentRole = AgentMode.PatchBuilder, Status = AgentOrchestrationStatus.Pending },
            new AgentOrchestrationStep { StepName = "Verifier", AgentRole = AgentMode.Verifier, Status = AgentOrchestrationStatus.Pending },
            new AgentOrchestrationStep { StepName = "Reviewer", AgentRole = AgentMode.Reviewer, Status = AgentOrchestrationStatus.Pending },
            new AgentOrchestrationStep { StepName = "Apply", AgentRole = AgentMode.ToolRunner, Status = AgentOrchestrationStatus.Pending },
            new AgentOrchestrationStep { StepName = "Git Sync", AgentRole = AgentMode.ToolRunner, Status = AgentOrchestrationStatus.Pending }
        ];
    }

    private async Task PersistRunAsync(AgentOrchestrationRun run, CancellationToken cancellationToken)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var runs = await LoadAsync(cancellationToken);
            var index = runs.FindIndex(item => item.Id.Equals(run.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                runs[index] = Clone(run);
            }
            else
            {
                runs.Add(Clone(run));
            }

            await SaveAsync(runs, cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task RecordRunCreatedAsync(AgentOrchestrationRun run, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = run.CreatedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.RunCreated,
            StepName = string.Empty,
            Message = $"Run created for task '{run.TaskName}'.",
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = run.Id
        }, cancellationToken);
    }

    private async Task RecordStepStartedAsync(AgentOrchestrationRun run, AgentOrchestrationStep step, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = step.StartedAtUtc == default ? DateTime.UtcNow : step.StartedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.StepStarted,
            StepName = step.StepName,
            Message = $"Started {step.StepName}.",
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordStepCompletedAsync(AgentOrchestrationRun run, AgentOrchestrationStep step, string message, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = step.CompletedAtUtc ?? DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.StepCompleted,
            StepName = step.StepName,
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordStepFailedAsync(AgentOrchestrationRun run, AgentOrchestrationStep step, string errorMessage, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = step.CompletedAtUtc ?? DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.StepFailed,
            StepName = step.StepName,
            Message = errorMessage,
            Severity = AgentOrchestrationTimelineSeverity.Error,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordSafetyReviewGeneratedAsync(
        AgentOrchestrationRun run,
        TaskPlanSlice slice,
        AgentOrchestrationSafetyReport report,
        CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = report.CreatedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.SafetyReviewGenerated,
            StepName = "Apply",
            Message = report.Summary,
            Severity = report.BlocksAutoApply || report.RequiresManualApproval
                ? AgentOrchestrationTimelineSeverity.Warning
                : AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = report.Id
        }, cancellationToken);
    }

    private async Task RecordApplySkippedAsync(
        AgentOrchestrationRun run,
        TaskPlanSlice slice,
        AgentOrchestrationSafetyReport report,
        CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.ApplySkipped,
            StepName = "Apply",
            Message = report.Summary,
            Severity = report.BlocksAutoApply
                ? AgentOrchestrationTimelineSeverity.Error
                : AgentOrchestrationTimelineSeverity.Warning,
            RelatedEntityId = slice.Id
        }, cancellationToken);
    }

    private async Task RecordApplyCompletedAsync(
        AgentOrchestrationRun run,
        TaskPlanSlice slice,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.ApplyCompleted,
            StepName = "Apply",
            Message = $"Applied {changedFiles.Count} file(s) successfully.",
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = slice.Id
        }, cancellationToken);
    }

    private async Task RecordGitSyncSkippedAsync(AgentOrchestrationRun run, string message, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.GitSyncSkipped,
            StepName = "Git Sync",
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = run.Id
        }, cancellationToken);
    }

    private async Task RecordGitSyncCompletedAsync(AgentOrchestrationRun run, string message, string commitHash, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.GitSyncCompleted,
            StepName = "Git Sync",
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = string.IsNullOrWhiteSpace(commitHash) ? run.Id : commitHash
        }, cancellationToken);
    }

    private async Task RecordGitSyncFailedAsync(AgentOrchestrationRun run, string message, string commitHash, CancellationToken cancellationToken)
    {
        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.GitSyncFailed,
            StepName = "Git Sync",
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Error,
            RelatedEntityId = string.IsNullOrWhiteSpace(commitHash) ? run.Id : commitHash
        }, cancellationToken);
    }

    private async Task RecordRetryRequestedAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        AgentOrchestrationRetryRequest request,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(request.Notes)
            ? $"Retry requested for failed step '{step.StepName}'."
            : $"Retry requested for failed step '{step.StepName}': {request.Notes.Trim()}";

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.RetryRequested,
            StepName = step.StepName,
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Warning,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordRetryCompletedAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        AgentOrchestrationRetryRequest request,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(request.RequestedBy)
            ? $"Retry completed for failed step '{step.StepName}'."
            : $"Retry completed for failed step '{step.StepName}' by {request.RequestedBy.Trim()}.";

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.RetryCompleted,
            StepName = step.StepName,
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordRetryFailedAsync(
        AgentOrchestrationRun run,
        AgentOrchestrationStep step,
        AgentOrchestrationRetryRequest request,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var message = string.IsNullOrWhiteSpace(request.RequestedBy)
            ? $"Retry failed for step '{step.StepName}': {errorMessage}"
            : $"Retry failed for step '{step.StepName}' by {request.RequestedBy.Trim()}: {errorMessage}";

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = DateTime.UtcNow,
            EventType = AgentOrchestrationTimelineEventType.RetryFailed,
            StepName = step.StepName,
            Message = message,
            Severity = AgentOrchestrationTimelineSeverity.Error,
            RelatedEntityId = step.Id
        }, cancellationToken);
    }

    private async Task RecordTimelineEventAsync(AgentOrchestrationTimelineEvent timelineEvent, CancellationToken cancellationToken)
    {
        try
        {
            await timelineService.AddAsync(timelineEvent, cancellationToken);
        }
        catch
        {
            // Timeline persistence should not block orchestration execution.
        }
    }

    private async Task<AgentOrchestrationCheckpoint> RecordPauseCheckpointAsync(
        AgentOrchestrationRun run,
        string currentStepName,
        string nextStepName,
        string message,
        CancellationToken cancellationToken)
    {
        var checkpoint = await checkpointService.CreateAsync(new AgentOrchestrationCheckpoint
        {
            RunId = run.Id,
            CreatedAtUtc = DateTime.UtcNow,
            CurrentStepName = currentStepName,
            NextStepName = nextStepName,
            Status = AgentOrchestrationStatus.Paused,
            Message = message
        }, cancellationToken);

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = checkpoint.CreatedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.PauseRequested,
            StepName = string.IsNullOrWhiteSpace(nextStepName) ? currentStepName : nextStepName,
            Message = string.IsNullOrWhiteSpace(message)
                ? $"Pause requested for run {run.TaskName}."
                : message,
            Severity = AgentOrchestrationTimelineSeverity.Warning,
            RelatedEntityId = run.Id
        }, cancellationToken);

        await RecordTimelineEventAsync(new AgentOrchestrationTimelineEvent
        {
            RunId = run.Id,
            TimestampUtc = checkpoint.CreatedAtUtc,
            EventType = AgentOrchestrationTimelineEventType.CheckpointCreated,
            StepName = string.IsNullOrWhiteSpace(nextStepName) ? currentStepName : nextStepName,
            Message = string.IsNullOrWhiteSpace(message)
                ? "Orchestration checkpoint created."
                : message,
            Severity = AgentOrchestrationTimelineSeverity.Info,
            RelatedEntityId = checkpoint.Id
        }, cancellationToken);

        return checkpoint;
    }

    private async Task<List<AgentOrchestrationRun>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetRunsPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentOrchestrationRun>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentOrchestrationRun> runs, CancellationToken cancellationToken)
    {
        var path = GetRunsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, runs, JsonOptions, cancellationToken);
    }

    private string GetRunsPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", RunsFileName);
    }

    private static AgentOrchestrationRun Clone(AgentOrchestrationRun run)
    {
        return new AgentOrchestrationRun
        {
            Id = run.Id,
            TaskName = run.TaskName,
            UserRequest = run.UserRequest,
            Status = run.Status,
            Steps = run.Steps.Select(Clone).ToList(),
            CreatedAtUtc = run.CreatedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            CommitAndSync = run.CommitAndSync,
            ApproveHighRiskApply = run.ApproveHighRiskApply,
            ExecutionPolicyName = run.ExecutionPolicyName,
            ApplySucceeded = run.ApplySucceeded,
            ApplyMessage = run.ApplyMessage,
            ApplyRiskGateMessage = run.ApplyRiskGateMessage,
            AppliedSliceIds = [.. (run.AppliedSliceIds ?? [])],
            AppliedFiles = [.. (run.AppliedFiles ?? [])],
            ApplyAuditIds = [.. (run.ApplyAuditIds ?? [])],
            CommitAttempted = run.CommitAttempted,
            CommitSucceeded = run.CommitSucceeded,
            CommitHash = run.CommitHash,
            PushAttempted = run.PushAttempted,
            PushSucceeded = run.PushSucceeded,
            GitMessage = run.GitMessage,
            HumanApprovalRequestId = run.HumanApprovalRequestId,
            HumanApprovalPending = run.HumanApprovalPending,
            PausedAtUtc = run.PausedAtUtc,
            PausedReason = run.PausedReason,
            NextStepIndex = run.NextStepIndex,
            CheckpointPlanId = run.CheckpointPlanId,
            CheckpointSliceId = run.CheckpointSliceId,
            CheckpointSliceTitle = run.CheckpointSliceTitle,
            CheckpointPatchPackageId = run.CheckpointPatchPackageId,
            CheckpointRiskLevel = run.CheckpointRiskLevel,
            SafetyReportId = run.SafetyReportId,
            SafetyReportCreatedAtUtc = run.SafetyReportCreatedAtUtc,
            SafetyHighestRiskLevel = run.SafetyHighestRiskLevel,
            SafetyTotalChangedFiles = run.SafetyTotalChangedFiles,
            SafetyRequiresManualApproval = run.SafetyRequiresManualApproval,
            SafetyBlocksAutoApply = run.SafetyBlocksAutoApply,
            SafetyReasons = [.. (run.SafetyReasons ?? [])],
            SafetySummary = run.SafetySummary
        };
    }

    private static AgentOrchestrationStep Clone(AgentOrchestrationStep step)
    {
        return new AgentOrchestrationStep
        {
            Id = step.Id,
            StepName = step.StepName,
            AgentRole = step.AgentRole,
            Status = step.Status,
            StartedAtUtc = step.StartedAtUtc,
            CompletedAtUtc = step.CompletedAtUtc,
            DurationMs = step.DurationMs,
            Success = step.Success,
            ErrorMessage = step.ErrorMessage,
            Message = step.Message,
            RelatedRunId = step.RelatedRunId
        };
    }

    private static long CalculateDuration(DateTime startUtc, DateTime endUtc)
    {
        if (startUtc == default || endUtc == default)
        {
            return 0;
        }

        return Math.Max(0, (long)(endUtc - startUtc).TotalMilliseconds);
    }

    private static void EnsureSliceReady(TaskPlanSlice? slice)
    {
        if (slice is null)
        {
            throw new InvalidOperationException("Planner did not produce a slice.");
        }
    }

    private static void EnsurePreviewReady(LocalCoderPatchPreview? preview)
    {
        if (preview is null)
        {
            throw new InvalidOperationException("Patch preview was not generated.");
        }
    }

    private static void EnsurePackageReady(PatchPackage? package)
    {
        if (package is null)
        {
            throw new InvalidOperationException("Patch package was not created.");
        }
    }

    private static void EnrichSliceFromRequest(TaskPlanSlice slice, string userRequest)
    {
        var createTargets = PatchIntentService.ExtractRequestedCreateFiles(userRequest)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (createTargets.Length > 0)
        {
            slice.TargetFiles = createTargets.ToList();
            slice.RelatedFiles = createTargets.ToList();
        }

    }

    private static string BuildApplyMessage(TaskSliceApplyResult applyResult)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(applyResult.Message))
        {
            parts.Add(applyResult.Message.Trim());
        }

        if (!string.IsNullOrWhiteSpace(applyResult.RiskGateMessage) &&
            !parts.Any(part => string.Equals(part, applyResult.RiskGateMessage, StringComparison.Ordinal)))
        {
            parts.Add(applyResult.RiskGateMessage.Trim());
        }

        if (!string.IsNullOrWhiteSpace(applyResult.RollbackMessage) &&
            !parts.Any(part => string.Equals(part, applyResult.RollbackMessage, StringComparison.Ordinal)))
        {
            parts.Add(applyResult.RollbackMessage.Trim());
        }

        return string.Join(" ", parts);
    }

    private static string BuildApplyDetails(TaskSliceApplyResult applyResult, IReadOnlyCollection<string> auditIds)
    {
        var details = new List<string>
        {
            $"Applied {applyResult.AppliedFilesCount} file(s) successfully."
        };

        if (auditIds.Count > 0)
        {
            details.Add($"Audit ids: {string.Join(", ", auditIds)}.");
        }

        return string.Join(" ", details);
    }

    private async Task<IReadOnlyList<string>> GetApplyAuditIdsAsync(
        string sliceId,
        ISet<string> existingAuditIds,
        CancellationToken cancellationToken)
    {
        var audits = await taskSliceApplyAuditService.GetLatestAsync(25, cancellationToken);
        return audits
            .Where(item => item.SliceId.Equals(sliceId, StringComparison.OrdinalIgnoreCase) && !existingAuditIds.Contains(item.Id))
            .OrderByDescending(item => item.TimestampUtc)
            .Select(item => item.Id)
            .ToArray();
    }

    private sealed record StepOutcome(string Message, string Details);
}
