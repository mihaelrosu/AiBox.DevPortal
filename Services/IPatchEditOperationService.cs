using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchEditOperationService
{
    Task<PatchEditOperationResult> BuildAsync(
        string projectPath,
        IReadOnlyList<LocalCoderFileContext> selectedFileContexts,
        string rawJson,
        CancellationToken cancellationToken = default);
}
