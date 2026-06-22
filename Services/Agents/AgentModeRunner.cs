using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services.Agents;


/// <summary>
/// Runs agent modes based on the provided profile and request.
/// </summary>
public sealed class AgentModeRunner(
    ICoderConsoleService coderConsoleService,
    IAgentRunHistoryService agentRunHistoryService,
    AgentInstructionService agentInstructionService,
    AgentModelRouteService agentModelRouteService,
    AgentModelRouteHealthCheckService agentModelRouteHealthCheckService,
    AgentExecutionPolicyService agentExecutionPolicyService,
    PatchSafetySnapshotService patchSafetySnapshotService,
    PatchApplyAuditService patchApplyAuditService,
    AgentRunTimelineService agentRunTimelineService) : IAgentModeRunner
{
    
    /// <summary>
    /// Creates a plan for the given project based on the provided request and mode profile.
    /// </summary>
    public async Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForCreatePlan(), "Create Plan");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureModelCallsAllowed(profile, policy, "Create Plan");
        var route = await ResolveRequiredModelRouteAsync(profile);
        await EnsureRouteHealthyAsync(route);
        var routedRequest = WithProfileModel(request, profile, route);
        var agentInstructionContext = await agentInstructionService.BuildContextAsync(routedRequest.ProjectPath, routedRequest.FileContexts.Select(fileContext => fileContext.RelativePath));
        var prompt = CoderConsoleService.BuildCreatePlanPrompt(
            routedRequest,
            BuildFileContextText(routedRequest.FileContexts),
            profile,
            BuildAgentInstructionsText(agentInstructionContext));

        try
        {
            var result = await coderConsoleService.CreatePlanAsync(routedRequest, profile);
            await RecordAsync(
                AgentActionProfiles.CreatePlanActionKey,
                profile,
                routedRequest.Model,
                routedRequest.Task,
                prompt,
                result.Plan,
                success: true,
                errorMessage: string.Empty);
            return result;
        }
        catch (Exception exception)
        {
            await RecordAsync(
                AgentActionProfiles.CreatePlanActionKey,
                profile,
                routedRequest.Model,
                routedRequest.Task,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Generates a patch preview for the given project based on the provided request and mode profile.
    /// </summary>
    public async Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request, AgentModeProfile profile, PatchPreviewRepairContext? repairContext = null)
    {
        EnsureMode(profile, AgentActionProfiles.ForGeneratePatchPreview(), "Generate Patch Preview");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureModelCallsAllowed(profile, policy, "Generate Patch Preview");
        EnsurePatchGenerationAllowed(profile, policy, "Generate Patch Preview");
        var route = await ResolveRequiredModelRouteAsync(profile);
        var timelineRunId = Guid.NewGuid().ToString("N");
        var timelineBaseTime = DateTimeOffset.UtcNow;
        try
        {
            await EnsureRouteHealthyAsync(route);
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddSeconds(-3),
                Stage = "RouteHealthCheck",
                Status = "Succeeded",
                Message = $"Model route '{route.Name}' is healthy at {route.BaseUrl}.",
                ReferenceId = route.Id
            });
        }
        catch (Exception exception)
        {
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddSeconds(-3),
                Stage = "RouteHealthCheck",
                Status = "Failed",
                Message = exception.Message,
                ReferenceId = route.Id
            });
            throw;
        }

        var routedRequest = WithProfileModel(request, profile, route);
        var agentInstructionContext = await agentInstructionService.BuildContextAsync(routedRequest.ProjectPath, routedRequest.FileContexts.Select(fileContext => fileContext.RelativePath));
        var intent = PatchIntentService.BuildIntent(routedRequest);
        var targetResolution = CoderConsoleService.ResolveDeterministicTargetResolution(
            routedRequest.Task,
            routedRequest.FileContexts,
            out _);
        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            routedRequest,
            BuildSelectedFilePathsText(routedRequest.FileContexts),
            BuildPatchPreviewFileContextText(routedRequest.FileContexts),
            CoderConsoleService.BuildPatchScopeText(routedRequest.AllowedPatchScope, routedRequest.AllowedPatchFolders, routedRequest.AllowedCreateFolders),
            PatchIntentService.BuildPromptText(intent),
            IsXmlDocumentationRequest(routedRequest.Task),
            targetResolution,
            repairContext,
            profile,
            BuildAgentInstructionsText(agentInstructionContext));

        LocalCoderPatchPreview result;
        try
        {
            result = await coderConsoleService.GeneratePatchPreviewAsync(routedRequest, profile, repairContext);
        }
        catch (Exception exception)
        {
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddSeconds(-1),
                Stage = "PatchPreviewGenerated",
                Status = "Failed",
                Message = exception.Message,
                ReferenceId = timelineRunId
            });
            await RecordAsync(
                AgentActionProfiles.GeneratePatchPreviewActionKey,
                profile,
                routedRequest.Model,
                routedRequest.Task,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }

        result.Id = timelineRunId;

        try
        {
            EnsurePatchPreviewWithinPolicy(result, policy);
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddSeconds(-2),
                Stage = "PolicyValidation",
                Status = "Succeeded",
                Message = $"Execution policy '{policy.Name}' accepted patch preview generation.",
                ReferenceId = policy.Id
            });
        }
        catch (Exception exception)
        {
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddSeconds(-2),
                Stage = "PolicyValidation",
                Status = "Failed",
                Message = exception.Message,
                ReferenceId = policy.Id
            });
            await RecordAsync(
                AgentActionProfiles.GeneratePatchPreviewActionKey,
                profile,
                routedRequest.Model,
                routedRequest.Task,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }

        await RecordTimelineAsync(new AgentRunTimelineItem
        {
            RunId = timelineRunId,
            Timestamp = timelineBaseTime.AddSeconds(-1),
            Stage = "PatchPreviewGenerated",
            Status = "Succeeded",
            Message = "Patch preview generated.",
            ReferenceId = result.Id
        });
        if (result.RequiresApproval)
        {
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = timelineRunId,
                Timestamp = timelineBaseTime.AddMilliseconds(-500),
                Stage = "ApprovalRequired",
                Status = "Required",
                Message = string.IsNullOrWhiteSpace(result.ApprovalReason)
                    ? "Patch preview requires human approval."
                    : result.ApprovalReason,
                ReferenceId = result.Id
            });
        }
        ApplyApprovalState(result, policy, profile);
        await RecordAsync(
            AgentActionProfiles.GeneratePatchPreviewActionKey,
            profile,
            routedRequest.Model,
            routedRequest.Task,
            prompt,
            result.PatchText,
            success: true,
            errorMessage: string.Empty);
        return result;
    }

    public async Task<LocalCoderPatchPreview> ApprovePatchPreviewAsync(LocalCoderPatchPreview patchPreview, AgentModeProfile profile, string? approvedBy = null)
    {
        EnsureMode(profile, AgentActionProfiles.ForGeneratePatchPreview(), "Approve Patch Preview");
        ArgumentNullException.ThrowIfNull(patchPreview);

        patchPreview.RequiresApproval = true;
        patchPreview.ApprovedAt = DateTimeOffset.UtcNow;
        patchPreview.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? Environment.UserName : approvedBy.Trim();
        await RecordTimelineAsync(new AgentRunTimelineItem
        {
            RunId = patchPreview.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Stage = "PreviewApproved",
            Status = "Succeeded",
            Message = string.IsNullOrWhiteSpace(patchPreview.ApprovalReason)
                ? "Patch preview approved."
                : patchPreview.ApprovalReason,
            ReferenceId = patchPreview.Id
        });
        await RecordPatchApplyAuditAsync(
            "Approved",
            patchPreview,
            profile,
            success: true,
            message: string.IsNullOrWhiteSpace(patchPreview.ApprovalReason)
                ? "Patch preview approved."
                : patchPreview.ApprovalReason,
            snapshotId: string.Empty,
            verificationSucceeded: false,
            verificationSummary: string.Empty,
            changedFiles: patchPreview.FileChanges.Select(change => change.RelativePath));
        return await Task.FromResult(patchPreview);
    }

    
    /// <summary>
    /// Applies the provided patch preview to the given project based on the provided mode profile.
    /// </summary>
    public async Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForApplyPatch(), "Apply Patch");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        ArgumentNullException.ThrowIfNull(patchPreview);

        if (!policy.AllowPatchApply)
        {
            var blockedMessage = $"Agent profile '{profile.Name}' does not allow patch apply required for Apply Patch.";
            await RecordPatchApplyAuditAsync(
                "ApplyBlocked",
                patchPreview,
                profile,
                success: false,
                message: blockedMessage,
                snapshotId: string.Empty,
                verificationSucceeded: false,
                verificationSummary: string.Empty,
                changedFiles: patchPreview.FileChanges.Select(change => change.RelativePath));
            throw new InvalidOperationException(blockedMessage);
        }

        if (patchPreview.RequiresApproval && patchPreview.ApprovedAt is null)
        {
            var blockedMessage = "Patch preview requires human approval before apply.";
            await RecordPatchApplyAuditAsync(
                "ApplyBlocked",
                patchPreview,
                profile,
                success: false,
                message: blockedMessage,
                snapshotId: string.Empty,
                verificationSucceeded: false,
                verificationSummary: string.Empty,
                changedFiles: patchPreview.FileChanges.Select(change => change.RelativePath));
            throw new InvalidOperationException(blockedMessage);
        }

        var safetySnapshot = await patchSafetySnapshotService.CreateAsync(patchPreview);
        if (!safetySnapshot.Success)
        {
            var blockedMessage = $"Patch safety snapshot failed: {safetySnapshot.Message}";
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = "SnapshotCreated",
                Status = "Failed",
                Message = blockedMessage,
                ReferenceId = safetySnapshot.Id
            });
            await RecordPatchApplyAuditAsync(
                "ApplyBlocked",
                patchPreview,
                profile,
                success: false,
                message: blockedMessage,
                snapshotId: safetySnapshot.Id,
                verificationSucceeded: false,
                verificationSummary: string.Empty,
                changedFiles: patchPreview.FileChanges.Select(change => change.RelativePath));
            throw new InvalidOperationException(blockedMessage);
        }

        await RecordTimelineAsync(new AgentRunTimelineItem
        {
            RunId = patchPreview.Id,
            Timestamp = DateTimeOffset.UtcNow,
            Stage = "SnapshotCreated",
            Status = "Succeeded",
            Message = safetySnapshot.Message,
            ReferenceId = safetySnapshot.Id
        });

        var prompt = BuildActionPrompt(
            AgentActionProfiles.ApplyPatchActionKey,
            profile,
            $"Apply the patch preview to project {patchPreview.ProjectPath}.",
            patchPreview.PatchText);

        try
        {
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = "PatchApplyStarted",
                Status = "Started",
                Message = "Patch apply started.",
                ReferenceId = safetySnapshot.Id
            });
            var result = await coderConsoleService.ApplyPatchPreviewAsync(patchPreview);
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = result.Applied ? "PatchApplySucceeded" : "PatchApplyFailed",
                Status = result.Applied ? "Succeeded" : "Failed",
                Message = result.Applied
                    ? "Patch applied successfully."
                    : (string.IsNullOrWhiteSpace(result.Message) ? "Patch apply failed." : result.Message),
                ReferenceId = safetySnapshot.Id
            });
            result.RequiresApproval = patchPreview.RequiresApproval;
            result.ApprovalReason = patchPreview.ApprovalReason;
            result.ApprovedAt = patchPreview.ApprovedAt;
            result.ApprovedBy = patchPreview.ApprovedBy;
            IReadOnlyList<CommandRunResult> verificationResults = [];
            if (result.Applied)
            {
                verificationResults = await coderConsoleService.VerifyProjectAsync(
                    patchPreview.ProjectPath,
                    patchPreview.VerificationCommands);
            }

            var verificationFailed = verificationResults.FirstOrDefault(item => item.ExitCode != 0);
            result.VerificationResults = verificationResults;
            result.VerificationRun = verificationFailed ?? verificationResults.LastOrDefault() ?? new CommandRunResult
            {
                Command = (patchPreview.VerificationCommands.Count > 0 ? patchPreview.VerificationCommands[0] : "dotnet build").Trim(),
                ExitCode = result.Applied ? 0 : -1
            };
            result.VerificationSucceeded = result.Applied && verificationFailed is null;
            result.VerificationOutput = SummarizeCommandResults(verificationResults);
            result.VerificationPassed = result.VerificationSucceeded;
            result.RestoreAttempted = false;
            result.RestoreSucceeded = false;
            result.RestoreMessage = string.Empty;

            if (result.Applied && !result.VerificationSucceeded)
            {
                result.Message = "Patch applied, but verification failed.";
                await RecordTimelineAsync(new AgentRunTimelineItem
                {
                    RunId = patchPreview.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = "VerificationFailed",
                    Status = "Failed",
                    Message = result.VerificationOutput,
                    ReferenceId = result.VerificationRun.Command
                });

                if (policy.AutoRestoreOnVerificationFailure)
                {
                    result.RestoreAttempted = true;
                    await RecordTimelineAsync(new AgentRunTimelineItem
                    {
                        RunId = patchPreview.Id,
                        Timestamp = DateTimeOffset.UtcNow,
                        Stage = "RestoreAttempted",
                        Status = "Attempted",
                        Message = "Restoring safety snapshot after verification failure.",
                        ReferenceId = safetySnapshot.Id
                    });
                    var restoreResult = await patchSafetySnapshotService.RestoreAsync(safetySnapshot.Id);
                    result.RestoreSucceeded = restoreResult.Success;
                    result.RestoreMessage = restoreResult.Message;
                    await RecordTimelineAsync(new AgentRunTimelineItem
                    {
                        RunId = patchPreview.Id,
                        Timestamp = DateTimeOffset.UtcNow,
                        Stage = restoreResult.Success ? "RestoreSucceeded" : "RestoreFailed",
                        Status = restoreResult.Success ? "Succeeded" : "Failed",
                        Message = restoreResult.Message,
                        ReferenceId = safetySnapshot.Id
                    });
                }
            }
            else if (result.Applied)
            {
                await RecordTimelineAsync(new AgentRunTimelineItem
                {
                    RunId = patchPreview.Id,
                    Timestamp = DateTimeOffset.UtcNow,
                    Stage = "VerificationSucceeded",
                    Status = "Succeeded",
                    Message = result.VerificationOutput,
                    ReferenceId = result.VerificationRun.Command
                });
            }

            var applySucceeded = result.Applied;
            await RecordPatchApplyAuditAsync(
                applySucceeded ? "ApplySucceeded" : "ApplyFailed",
                patchPreview,
                profile,
                success: applySucceeded,
                message: applySucceeded
                    ? (string.IsNullOrWhiteSpace(result.Message) ? "Patch applied successfully." : result.Message)
                    : result.Message,
                snapshotId: safetySnapshot.Id,
                verificationSucceeded: result.VerificationSucceeded,
                verificationSummary: BuildVerificationSummary(result),
                restoreAttempted: result.RestoreAttempted,
                restoreSucceeded: result.RestoreSucceeded,
                restoreSummary: BuildRestoreSummary(result),
                changedFiles: result.ChangedFiles.Count > 0
                    ? result.ChangedFiles
                    : patchPreview.FileChanges.Select(change => change.RelativePath));
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = "AuditWritten",
                Status = "Succeeded",
                Message = applySucceeded ? "Patch apply audit written." : "Patch apply audit written for failed apply.",
                ReferenceId = patchPreview.Id
            });
            await RecordAsync(
                AgentActionProfiles.ApplyPatchActionKey,
                profile,
                profile.Model,
                $"Apply patch preview for {patchPreview.ProjectPath}",
                prompt,
                BuildApplyResultText(result),
                success: applySucceeded,
                errorMessage: applySucceeded && !result.VerificationSucceeded ? result.Message : (applySucceeded ? string.Empty : result.Message));
            return result;
        }
        catch (Exception exception)
        {
            await RecordPatchApplyAuditAsync(
                "ApplyFailed",
                patchPreview,
                profile,
                success: false,
                message: exception.Message,
                snapshotId: safetySnapshot.Id,
                verificationSucceeded: false,
                verificationSummary: string.Empty,
                changedFiles: patchPreview.FileChanges.Select(change => change.RelativePath));
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = "PatchApplyFailed",
                Status = "Failed",
                Message = exception.Message,
                ReferenceId = safetySnapshot.Id
            });
            await RecordTimelineAsync(new AgentRunTimelineItem
            {
                RunId = patchPreview.Id,
                Timestamp = DateTimeOffset.UtcNow,
                Stage = "AuditWritten",
                Status = "Succeeded",
                Message = "Patch apply audit written for failed apply.",
                ReferenceId = patchPreview.Id
            });
            await RecordAsync(
                AgentActionProfiles.ApplyPatchActionKey,
                profile,
                profile.Model,
                $"Apply patch preview for {patchPreview.ProjectPath}",
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Rolls back the applied patch in the given project based on the provided mode profile.
    /// </summary>
public async Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(LocalCoderPatchApplyResult applyResult, string projectPath, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForRollbackPatch(), "Rollback Patch");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsurePatchApplyAllowed(profile, policy, "Rollback Patch");
        var prompt = BuildActionPrompt(
            AgentActionProfiles.RollbackPatchActionKey,
            profile,
            $"Rollback the applied patch in project {projectPath}.",
            applyResult.Message);

        try
        {
            var result = await coderConsoleService.RollbackPatchAsync(applyResult, projectPath);
        await RecordAsync(
            AgentActionProfiles.RollbackPatchActionKey,
            profile,
            profile.Model,
            $"Rollback patch for {projectPath}",
            prompt,
            BuildRollbackResultText(result),
                success: result.RolledBack && result.VerificationPassed,
                errorMessage: result.RolledBack && result.VerificationPassed ? string.Empty : result.Message);
            return result;
        }
        catch (Exception exception)
        {
        await RecordAsync(
            AgentActionProfiles.RollbackPatchActionKey,
            profile,
            profile.Model,
            $"Rollback patch for {projectPath}",
            prompt,
            string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Commits changes in the given project based on the provided commit message and mode profile.
    /// </summary>
public async Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForCommitChanges(), "Commit Changes");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureShellCommandsAllowed(profile, policy, "Commit Changes");
        EnsureToolCallsAllowed(profile, policy, "Commit Changes");
        var userRequest = $"Commit changes in {projectPath}";
        var prompt = BuildActionPrompt(
            AgentActionProfiles.CommitChangesActionKey,
            profile,
            $"Run git status, build, diff, add, commit, and deploy in {projectPath}.",
            commitMessage);

        try
        {
            var result = await coderConsoleService.CommitChangesAsync(projectPath, commitMessage);
            var failure = result.FirstOrDefault(item => item.ExitCode != 0);
        await RecordAsync(
            AgentActionProfiles.CommitChangesActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            SummarizeCommandResults(result),
                success: failure is null,
                errorMessage: failure is null ? string.Empty : $"{failure.Command} exited {failure.ExitCode}");
            return result;
        }
        catch (Exception exception)
        {
        await RecordAsync(
            AgentActionProfiles.CommitChangesActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Builds and deploys the given project based on the provided mode profile.
    /// </summary>
public async Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForBuildDeploy(), "Build & Deploy");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureShellCommandsAllowed(profile, policy, "Build & Deploy");
        EnsureToolCallsAllowed(profile, policy, "Build & Deploy");
        var userRequest = $"Build and deploy {projectPath}";
        var prompt = BuildActionPrompt(
            AgentActionProfiles.BuildDeployActionKey,
            profile,
            $"Run dotnet build and docker compose up -d --build aibox-devportal in {projectPath}.",
            projectPath);

        try
        {
            var result = await coderConsoleService.BuildDeployAsync(projectPath);
            var failure = result.FirstOrDefault(item => item.ExitCode != 0);
        await RecordAsync(
            AgentActionProfiles.BuildDeployActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            SummarizeCommandResults(result),
                success: failure is null,
                errorMessage: failure is null ? string.Empty : $"{failure.Command} exited {failure.ExitCode}");
            return result;
        }
        catch (Exception exception)
        {
        await RecordAsync(
            AgentActionProfiles.BuildDeployActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Runs the specified command in the given project based on the provided mode profile.
    /// </summary>
public async Task<CommandRunResult> RunCommandAsync(string projectPath, string command, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForRunCommand(), "Run Command");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureShellCommandsAllowed(profile, policy, "Run Command");
        EnsureToolCallsAllowed(profile, policy, "Run Command");
        var userRequest = command;
        var prompt = BuildActionPrompt(
            AgentActionProfiles.RunCommandActionKey,
            profile,
            $"Run an approved command in {projectPath}.",
            command);

        try
        {
            var result = await coderConsoleService.RunCommandAsync(projectPath, command);
        await RecordAsync(
            AgentActionProfiles.RunCommandActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            SummarizeCommandResult(result),
                success: result.ExitCode == 0,
                errorMessage: result.ExitCode == 0 ? string.Empty : $"{result.Command} exited {result.ExitCode}");
            return result;
        }
        catch (Exception exception)
        {
        await RecordAsync(
            AgentActionProfiles.RunCommandActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    
    /// <summary>
    /// Verifies the given project based on the provided mode profile and optional verification commands.
    /// </summary>
public async Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath, AgentModeProfile profile, IReadOnlyList<string>? verificationCommands = null)
    {
        EnsureMode(profile, AgentActionProfiles.ForVerifyProject(), "Verify Project");
        var policy = await ResolveRequiredExecutionPolicyAsync(profile);
        EnsureShellCommandsAllowed(profile, policy, "Verify Project");
        var userRequest = $"Verify project {projectPath}";
        var prompt = BuildActionPrompt(
            AgentActionProfiles.VerifyProjectActionKey,
            profile,
            $"Run the approved verification checks in {projectPath}.",
            projectPath);

        try
        {
            var result = await coderConsoleService.VerifyProjectAsync(projectPath, verificationCommands);
            var failure = result.FirstOrDefault(item => item.ExitCode != 0);
        await RecordAsync(
            AgentActionProfiles.VerifyProjectActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            SummarizeCommandResults(result),
                success: failure is null,
                errorMessage: failure is null ? string.Empty : $"{failure.Command} exited {failure.ExitCode}");
            return result;
        }
        catch (Exception exception)
        {
        await RecordAsync(
            AgentActionProfiles.VerifyProjectActionKey,
            profile,
            profile.Model,
            userRequest,
            prompt,
            string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    private async Task RecordAsync(
        string actionKey,
        AgentModeProfile profile,
        string model,
        string userRequest,
        string promptSent,
        string resultText,
        bool success,
        string errorMessage)
    {
        await agentRunHistoryService.AddAsync(new AgentRunRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            ActionKey = actionKey,
            ProfileMode = profile.Mode,
            Model = model,
            UserRequest = userRequest,
            PromptSent = promptSent,
            ResultText = resultText,
            Success = success,
            ErrorMessage = errorMessage
        });
    }

    private async Task RecordTimelineAsync(AgentRunTimelineItem item)
    {
        try
        {
            await agentRunTimelineService.AddAsync(item);
        }
        catch
        {
        }
    }

    private async Task RecordPatchApplyAuditAsync(
        string action,
        LocalCoderPatchPreview patchPreview,
        AgentModeProfile profile,
        bool success,
        string message,
        string snapshotId,
        bool verificationSucceeded,
        string verificationSummary,
        IEnumerable<string> changedFiles,
        bool restoreAttempted = false,
        bool restoreSucceeded = false,
        string restoreSummary = "")
    {
        ArgumentNullException.ThrowIfNull(patchPreview);
        ArgumentNullException.ThrowIfNull(profile);

        await patchApplyAuditService.AddAsync(new PatchApplyAuditItem
        {
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            PatchPreviewId = patchPreview.Id,
            ProfileId = profile.Id,
            PolicyId = profile.PolicyId,
            ModelRouteId = profile.ModelRouteId,
            SnapshotId = snapshotId,
            ApprovedBy = patchPreview.ApprovedBy,
            ApprovedAt = patchPreview.ApprovedAt,
            Success = success,
            Message = message,
            ChangedFiles = changedFiles
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            VerificationSucceeded = verificationSucceeded,
            VerificationSummary = verificationSummary,
            RestoreAttempted = restoreAttempted,
            RestoreSucceeded = restoreSucceeded,
            RestoreSummary = restoreSummary
        });
    }

    private static string BuildVerificationSummary(LocalCoderPatchApplyResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Verification succeeded: {result.VerificationSucceeded}");

        if (result.VerificationRun is not null && !string.IsNullOrWhiteSpace(result.VerificationRun.Command))
        {
            builder.AppendLine($"Verification run: {result.VerificationRun.Command} (exit {result.VerificationRun.ExitCode})");
        }

        if (!string.IsNullOrWhiteSpace(result.VerificationOutput))
        {
            builder.AppendLine("Verification output:");
            builder.AppendLine(result.VerificationOutput);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildRestoreSummary(LocalCoderPatchApplyResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Restore attempted: {result.RestoreAttempted}");
        builder.AppendLine($"Restore succeeded: {result.RestoreSucceeded}");

        if (!string.IsNullOrWhiteSpace(result.RestoreMessage))
        {
            builder.AppendLine("Restore message:");
            builder.AppendLine(result.RestoreMessage);
        }

        return builder.ToString().TrimEnd();
    }

    private static void EnsureMode(AgentModeProfile profile, AgentMode expectedMode, string actionName)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Mode != expectedMode)
        {
            throw new InvalidOperationException($"{actionName} requires the {expectedMode} profile.");
        }
    }

    private async Task<AgentExecutionPolicy> ResolveRequiredExecutionPolicyAsync(AgentModeProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.PolicyId))
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not have a valid execution policy.");
        }

        var policy = await agentExecutionPolicyService.GetByIdAsync(profile.PolicyId, cancellationToken);
        if (policy is null)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not have a valid execution policy.");
        }

        return policy;
    }

    private static void EnsureModelCallsAllowed(AgentModeProfile profile, AgentExecutionPolicy policy, string actionName)
    {
        if (!policy.AllowModelCalls)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not allow model calls required for {actionName}.");
        }
    }

    private static void EnsureToolCallsAllowed(AgentModeProfile profile, AgentExecutionPolicy policy, string actionName)
    {
        if (!policy.AllowToolCalls)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not allow tool calls required for {actionName}.");
        }
    }

    private static void EnsureShellCommandsAllowed(AgentModeProfile profile, AgentExecutionPolicy policy, string actionName)
    {
        if (!policy.AllowShellCommands)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not allow shell commands required for {actionName}.");
        }
    }

    private static void EnsurePatchGenerationAllowed(AgentModeProfile profile, AgentExecutionPolicy policy, string actionName)
    {
        if (!policy.AllowPatchGeneration)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not allow patch generation required for {actionName}.");
        }
    }

    private static void EnsurePatchApplyAllowed(AgentModeProfile profile, AgentExecutionPolicy policy, string actionName)
    {
        if (!policy.AllowPatchApply)
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not allow patch apply required for {actionName}.");
        }
    }

    private static void ApplyApprovalState(LocalCoderPatchPreview preview, AgentExecutionPolicy policy, AgentModeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(profile);

        if (!policy.RequireHumanApproval)
        {
            preview.RequiresApproval = false;
            preview.ApprovalReason = string.Empty;
            preview.ApprovedAt = null;
            preview.ApprovedBy = string.Empty;
            return;
        }

        preview.RequiresApproval = true;
        preview.ApprovalReason = "Execution policy requires human approval before apply.";
        preview.ApprovedAt = null;
        preview.ApprovedBy = string.Empty;
    }

    private static void EnsurePatchPreviewWithinPolicy(LocalCoderPatchPreview preview, AgentExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(policy);

        var fileChangeCount = GetPatchFileChangeCount(preview);
        if (policy.MaxFilesChanged > 0 && fileChangeCount > policy.MaxFilesChanged)
        {
            throw new InvalidOperationException($"Patch changes too many files. Limit is {policy.MaxFilesChanged}.");
        }

        if (policy.MaxPatchOperations > 0 && fileChangeCount > policy.MaxPatchOperations)
        {
            throw new InvalidOperationException($"Patch has too many operations. Limit is {policy.MaxPatchOperations}.");
        }

        var targetDirectories = GetPatchTargetDirectories(preview.FileChanges)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blockedDirectories = NormalizePolicyDirectories(policy.BlockedDirectories);
        var allowedDirectories = NormalizePolicyDirectories(policy.AllowedDirectories);

        foreach (var targetDirectory in targetDirectories)
        {
            var blockedDirectory = blockedDirectories.FirstOrDefault(blocked => IsDirectoryUnder(targetDirectory, blocked));
            if (!string.IsNullOrWhiteSpace(blockedDirectory))
            {
                throw new InvalidOperationException($"Patch targets blocked directory: {targetDirectory}");
            }
        }

        if (allowedDirectories.Count > 0)
        {
            foreach (var targetDirectory in targetDirectories)
            {
                if (allowedDirectories.Any(allowed => IsDirectoryUnder(targetDirectory, allowed)))
                {
                    continue;
                }

                throw new InvalidOperationException($"Patch targets directory not allowed by policy: {targetDirectory}");
            }
        }
    }

    private static int GetPatchFileChangeCount(LocalCoderPatchPreview preview)
    {
        if (preview.FileChanges.Count > 0)
        {
            return preview.FileChanges.Count;
        }

        return CountDiffFileHeaders(preview.PatchText);
    }

    private static int CountDiffFileHeaders(string patchText)
    {
        if (string.IsNullOrWhiteSpace(patchText))
        {
            return 0;
        }

        return patchText
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Count(line => line.StartsWith("diff --git ", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> GetPatchTargetDirectories(IReadOnlyList<PatchFileChange> fileChanges)
    {
        return fileChanges
            .Select(change => NormalizePatchTargetDirectory(change.RelativePath))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizePolicyDirectories(IReadOnlyList<string>? directories)
    {
        return (directories ?? [])
            .Select(NormalizePatchTargetDirectory)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .ToArray();
    }

    private static string NormalizePatchTargetDirectory(string relativePath)
    {
        var normalizedPath = (relativePath ?? string.Empty)
            .Replace('\\', '/')
            .Trim();

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return string.Empty;
        }

        var lastSlashIndex = normalizedPath.LastIndexOf('/');
        if (lastSlashIndex < 0)
        {
            return ".";
        }

        return normalizedPath[..(lastSlashIndex + 1)];
    }

    private static bool IsDirectoryUnder(string targetDirectory, string policyDirectory)
    {
        if (string.IsNullOrWhiteSpace(policyDirectory))
        {
            return true;
        }

        if (string.Equals(policyDirectory, ".", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(targetDirectory, ".", StringComparison.OrdinalIgnoreCase);
        }

        return targetDirectory.StartsWith(policyDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalCoderRequest WithProfileModel(LocalCoderRequest request, AgentModeProfile profile, AgentModelRoute? route = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);

        return new LocalCoderRequest
        {
            ProjectPath = request.ProjectPath,
            Model = !string.IsNullOrWhiteSpace(route?.Model)
                ? route.Model
                : string.IsNullOrWhiteSpace(profile.Model) ? request.Model : profile.Model,
            Task = request.Task,
            FileContexts = request.FileContexts.ToList(),
            AllowedPatchScope = request.AllowedPatchScope,
            AllowedPatchFolders = request.AllowedPatchFolders.ToList(),
            AllowedCreateFolders = request.AllowedCreateFolders.ToList()
        };
    }

    private async Task<AgentModelRoute> ResolveRequiredModelRouteAsync(AgentModeProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var route = await ResolveOptionalModelRouteAsync(profile, cancellationToken);
        if (route is null || string.IsNullOrWhiteSpace(route.Model))
        {
            throw new InvalidOperationException($"Agent profile '{profile.Name}' does not have a valid model route.");
        }

        return route;
    }

    private async Task<AgentModelRoute?> ResolveOptionalModelRouteAsync(AgentModeProfile profile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ModelRouteId))
        {
            return null;
        }

        var routes = await agentModelRouteService.GetAllAsync(cancellationToken);
        return routes.FirstOrDefault(route => route.Id.Equals(profile.ModelRouteId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureRouteHealthyAsync(AgentModelRoute route, CancellationToken cancellationToken = default)
    {
        var health = await agentModelRouteHealthCheckService.CheckAsync(route, cancellationToken);
        if (!health.IsHealthy)
        {
            throw new InvalidOperationException(health.Message);
        }
    }

    private static string BuildFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided.";
        }

        var builder = new StringBuilder();

        foreach (var fileContext in fileContexts)
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, 12_000));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildPatchPreviewFileContextText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file context was provided. No files are currently loaded.";
        }

        var builder = new StringBuilder();

        foreach (var fileContext in fileContexts)
        {
            builder.AppendLine($"FILE: {fileContext.RelativePath}");
            builder.AppendLine("```text");
            builder.AppendLine(TrimForPrompt(fileContext.Content, 12_000));
            builder.AppendLine("```");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildAgentInstructionsText(AgentInstructionContext instructionContext)
    {
        if (!instructionContext.HasFiles)
        {
            return "No relevant AGENTS.md files were found.";
        }

        return instructionContext.CombinedText;
    }

    private static string BuildSelectedFilePathsText(IReadOnlyList<LocalCoderFileContext> fileContexts)
    {
        if (fileContexts.Count == 0)
        {
            return "No selected file paths were provided.";
        }

        return string.Join(Environment.NewLine, fileContexts.Select(fileContext => $"- {fileContext.RelativePath}"));
    }

    private static string BuildPatchScopeText(PatchScopeMode scopeMode, IReadOnlyList<string> allowedFolders)
    {
        return scopeMode switch
        {
            PatchScopeMode.ContextFilesOnly => "Context Files Only",
            PatchScopeMode.SelectedFolders when allowedFolders.Count > 0 => string.Join(
                Environment.NewLine,
                [
                    "Selected Folders",
                    "Allowed folders:",
                    string.Join(Environment.NewLine, allowedFolders.Select(folder => $"- {folder}"))
                ]),
            PatchScopeMode.SelectedFolders => "Selected Folders (no folders configured)",
            PatchScopeMode.AnyProjectFile => "Any Project File",
            _ => "Context Files Only"
        };
    }

    private static bool IsXmlDocumentationRequest(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return false;
        }

        return task.Contains("xml documentation", StringComparison.OrdinalIgnoreCase) ||
               task.Contains("xml comments", StringComparison.OrdinalIgnoreCase) ||
               task.Contains("documentation comments", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildActionPrompt(string actionKey, AgentModeProfile profile, string instruction, string context)
    {
        return $"""
        Agent action: {actionKey}
        Profile mode: {profile.Mode}
        Profile name: {profile.Name}
        Profile model: {profile.Model}
        Rules summary: {profile.RulesSummary}

        Instruction:
        {instruction}

        Context:
        {context}
        """;
    }

    private static string SummarizeCommandResults(IReadOnlyList<CommandRunResult> results)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            results.Select(SummarizeCommandResult));
    }

    private static string SummarizeCommandResult(CommandRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Command: {result.Command}");
        builder.AppendLine($"Exit code: {result.ExitCode}");

        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            builder.AppendLine("Output:");
            builder.AppendLine(TrimText(result.Output, 8000));
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine("Error:");
            builder.AppendLine(TrimText(result.Error, 4000));
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildApplyResultText(LocalCoderPatchApplyResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Applied: {result.Applied}");
        builder.AppendLine($"Verification passed: {result.VerificationPassed}");
        builder.AppendLine(result.Message);
        builder.AppendLine($"Restore attempted: {result.RestoreAttempted}");
        builder.AppendLine($"Restore succeeded: {result.RestoreSucceeded}");

        if (!string.IsNullOrWhiteSpace(result.RestoreMessage))
        {
            builder.AppendLine("Restore message:");
            builder.AppendLine(result.RestoreMessage);
        }

        if (result.VerificationRun is not null && !string.IsNullOrWhiteSpace(result.VerificationRun.Command))
        {
            builder.AppendLine();
            builder.AppendLine($"Verification run: {result.VerificationRun.Command}");
            builder.AppendLine($"Verification exit code: {result.VerificationRun.ExitCode}");
        }

        if (!string.IsNullOrWhiteSpace(result.VerificationOutput))
        {
            builder.AppendLine();
            builder.AppendLine("Verification output:");
            builder.AppendLine(result.VerificationOutput);
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildRollbackResultText(LocalCoderPatchRollbackResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Rolled back: {result.RolledBack}");
        builder.AppendLine($"Verification passed: {result.VerificationPassed}");
        builder.AppendLine(result.Message);
        return builder.ToString().TrimEnd();
    }

    private static string TrimText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text ?? string.Empty;
        }

        return text[..maxChars];
    }

    private static string TrimForPrompt(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content ?? string.Empty;
        }

        return content[..maxChars];
    }
}
