using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IComfyUiService
{
    Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<ServiceHealth> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComfyUiWorkflowFile>> GetWorkflowsAsync(CancellationToken cancellationToken = default);
    Task<string> GetWorkflowAsync(string fileName, CancellationToken cancellationToken = default);
    Task SaveWorkflowAsync(string fileName, string json, CancellationToken cancellationToken = default);
    Task<string> BackupWorkflowsAsync(CancellationToken cancellationToken = default);
    Task<ComfyUiQueueResult> QueuePromptAsync(string workflowJson, CancellationToken cancellationToken = default);
    Task<ComfyUiGenerationResult> QueuePromptAndWaitForImageAsync(string workflowJson, long seed, CancellationToken cancellationToken = default);
    Task<string> SaveInputImageAsync(string fileName, Stream stream, CancellationToken cancellationToken = default);
    Task<ComfyUiModelInventory> GetModelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetCheckpointsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetLorasAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetUpscalersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetControlNetModelsAsync(CancellationToken cancellationToken = default);
}
