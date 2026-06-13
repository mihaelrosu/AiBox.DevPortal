using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services.Agents;

public sealed class AgentModeRunner(
    ICoderConsoleService coderConsoleService,
    IAgentRunHistoryService agentRunHistoryService) : IAgentModeRunner
{
    public async Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForCreatePlan(), "Create Plan");
        var routedRequest = WithProfileModel(request, profile);
        var prompt = CoderConsoleService.BuildCreatePlanPrompt(
            routedRequest,
            BuildFileContextText(routedRequest.FileContexts),
            profile);

        try
        {
            var result = await coderConsoleService.CreatePlanAsync(routedRequest, profile);
            await RecordAsync(
                AgentActionProfiles.CreatePlanActionKey,
                profile,
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
                routedRequest.Task,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request, AgentModeProfile profile, PatchPreviewRepairContext? repairContext = null)
    {
        EnsureMode(profile, AgentActionProfiles.ForGeneratePatchPreview(), "Generate Patch Preview");
        var routedRequest = WithProfileModel(request, profile);
        var intent = PatchIntentService.BuildIntent(routedRequest);
        var targetResolution = CoderConsoleService.ResolveDeterministicTargetResolution(
            routedRequest.Task,
            routedRequest.FileContexts,
            out _);
        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            routedRequest,
            BuildSelectedFilePathsText(routedRequest.FileContexts),
            BuildPatchPreviewFileContextText(routedRequest.FileContexts),
            BuildPatchScopeText(routedRequest.AllowedPatchScope, routedRequest.AllowedPatchFolders),
            PatchIntentService.BuildPromptText(intent),
            IsXmlDocumentationRequest(routedRequest.Task),
            targetResolution,
            repairContext,
            profile);

        try
        {
            var result = await coderConsoleService.GeneratePatchPreviewAsync(routedRequest, profile, repairContext);
            await RecordAsync(
                AgentActionProfiles.GeneratePatchPreviewActionKey,
                profile,
                routedRequest.Task,
                prompt,
                result.PatchText,
                success: true,
                errorMessage: string.Empty);
            return result;
        }
        catch (Exception exception)
        {
            await RecordAsync(
                AgentActionProfiles.GeneratePatchPreviewActionKey,
                profile,
                routedRequest.Task,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForApplyPatch(), "Apply Patch");
        var prompt = BuildActionPrompt(
            AgentActionProfiles.ApplyPatchActionKey,
            profile,
            $"Apply the patch preview to project {patchPreview.ProjectPath}.",
            patchPreview.PatchText);

        try
        {
            var result = await coderConsoleService.ApplyPatchPreviewAsync(patchPreview);
            await RecordAsync(
                AgentActionProfiles.ApplyPatchActionKey,
                profile,
                $"Apply patch preview for {patchPreview.ProjectPath}",
                prompt,
                BuildApplyResultText(result),
                success: result.Applied && result.VerificationPassed,
                errorMessage: result.Applied && result.VerificationPassed ? string.Empty : result.Message);
            return result;
        }
        catch (Exception exception)
        {
            await RecordAsync(
                AgentActionProfiles.ApplyPatchActionKey,
                profile,
                $"Apply patch preview for {patchPreview.ProjectPath}",
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(LocalCoderPatchApplyResult applyResult, string projectPath, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForRollbackPatch(), "Rollback Patch");
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
                $"Rollback patch for {projectPath}",
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForCommitChanges(), "Commit Changes");
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
                userRequest,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForBuildDeploy(), "Build & Deploy");
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
                userRequest,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<CommandRunResult> RunCommandAsync(string projectPath, string command, AgentModeProfile profile)
    {
        EnsureMode(profile, AgentActionProfiles.ForRunCommand(), "Run Command");
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
                userRequest,
                prompt,
                string.Empty,
                success: false,
                errorMessage: exception.Message);
            throw;
        }
    }

    public async Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath, AgentModeProfile profile, IReadOnlyList<string>? verificationCommands = null)
    {
        EnsureMode(profile, AgentActionProfiles.ForVerifyProject(), "Verify Project");
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
            Model = profile.Model,
            UserRequest = userRequest,
            PromptSent = promptSent,
            ResultText = resultText,
            Success = success,
            ErrorMessage = errorMessage
        });
    }

    private static void EnsureMode(AgentModeProfile profile, AgentMode expectedMode, string actionName)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.Mode != expectedMode)
        {
            throw new InvalidOperationException($"{actionName} requires the {expectedMode} profile.");
        }
    }

    private static LocalCoderRequest WithProfileModel(LocalCoderRequest request, AgentModeProfile profile)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);

        return new LocalCoderRequest
        {
            ProjectPath = request.ProjectPath,
            Model = string.IsNullOrWhiteSpace(profile.Model) ? request.Model : profile.Model,
            Task = request.Task,
            FileContexts = request.FileContexts.ToList()
        };
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
