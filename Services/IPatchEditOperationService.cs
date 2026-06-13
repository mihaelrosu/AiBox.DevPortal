using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPatchEditOperationService
{
    Task<PatchEditOperationResult> BuildAsync(
        string projectPath,
        IReadOnlyList<LocalCoderFileContext> selectedFileContexts,
        string rawJson,
        PatchIntent? intent = null,
        CancellationToken cancellationToken = default);
}
