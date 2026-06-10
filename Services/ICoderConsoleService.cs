using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ICoderConsoleService
{
    Task<IReadOnlyList<string>> GetWorkspaceRootsAsync();
    Task<IReadOnlyList<string>> GetOllamaModelsAsync();
    Task<IReadOnlyList<ProjectFileItem>> GetProjectFilesAsync(string projectPath);
    Task<IReadOnlyList<LocalCoderFileContext>> ReadFileContextsAsync(string projectPath, IReadOnlyList<string> relativePaths);
    Task<LocalCoderTask> CreatePlanAsync(LocalCoderRequest request);
    Task<LocalCoderPatchPreview> GeneratePatchPreviewAsync(LocalCoderRequest request);
    Task<LocalCoderPatchApplyResult> ApplyPatchPreviewAsync(LocalCoderPatchPreview patchPreview);
    Task<LocalCoderPatchRollbackResult> RollbackPatchAsync(LocalCoderPatchApplyResult applyResult, string projectPath);
    Task<CommandRunResult> RunCommandAsync(string projectPath, string command);
    Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath);
    Task<IReadOnlyList<LocalCoderTask>> GetHistoryAsync();
}
