using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services.Agents;

public interface IAgentModeRunner
{
    Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request, AgentModeProfile profile);
    Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request, AgentModeProfile profile, PatchPreviewRepairContext? repairContext = null);
    Task<LocalCoderPatchPreview> ApprovePatchPreviewAsync(LocalCoderPatchPreview patchPreview, AgentModeProfile profile, string? approvedBy = null);
    Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview, AgentModeProfile profile);
    Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(LocalCoderPatchApplyResult applyResult, string projectPath, AgentModeProfile profile);
    Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage, AgentModeProfile profile);
    Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath, AgentModeProfile profile);
    Task<CommandRunResult> RunCommandAsync(string projectPath, string command, AgentModeProfile profile);
    Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath, AgentModeProfile profile, IReadOnlyList<string>? verificationCommands = null);
}
