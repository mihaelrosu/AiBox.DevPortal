using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ILocalCoderService
{
    Task<IReadOnlyList<string>> GetWorkspaceRootsAsync();
    Task<IReadOnlyList<string>> GetOllamaModelsAsync();
    Task<LocalCoderTask> CreatePlanAsync(LocalCoderRequest request);
    Task<CommandRunResult> RunCommandAsync(string projectPath, string command);
    Task<IReadOnlyList<LocalCoderTask>> GetHistoryAsync();
}