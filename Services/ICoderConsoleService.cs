using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Services;

public interface ICoderConsoleService
{
    Task<IReadOnlyList<string>> GetWorkspaceRootsAsync();
    Task<IReadOnlyList<string>> GetOllamaModelsAsync();
    Task<IReadOnlyList<ProjectFileItem>> GetProjectFilesAsync(string projectPath);
    Task<IReadOnlyList<LocalCoderFileContext>> ReadFileContextsAsync(string projectPath, IReadOnlyList<string> relativePaths);
    Task<ConsoleLocalCoderTask> CreatePlanAsync(LocalCoderRequest request, AgentModeProfile? profile = null);
    Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request, AgentModeProfile? profile = null, PatchPreviewRepairContext? repairContext = null);
    Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview);
    Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(LocalCoderPatchApplyResult applyResult, string projectPath);
    Task<IReadOnlyList<CommandRunResult>> CommitChangesAsync(string projectPath, string commitMessage);
    Task<IReadOnlyList<CommandRunResult>> BuildDeployAsync(string projectPath);
    Task<CommandRunResult> RunCommandAsync(string projectPath, string command);
    Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath, IReadOnlyList<string>? commands = null);
    Task<IReadOnlyList<ConsoleLocalCoderTask>> GetHistoryAsync();
}
