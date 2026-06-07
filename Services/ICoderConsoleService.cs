using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ICoderConsoleService
{
    Task<IReadOnlyList<string>> GetWorkspaceRootsAsync();
    Task<IReadOnlyList<string>> GetOllamaModelsAsync();
    Task<LocalCoderTask> CreatePlanAsync(LocalCoderRequest request);
    Task<CommandRunResult> RunCommandAsync(string projectPath, string command);
    Task<IReadOnlyList<CommandRunResult>> VerifyProjectAsync(string projectPath);
    Task<IReadOnlyList<LocalCoderTask>> GetHistoryAsync();
}