using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface ILocalCoderContextService
{
    Task<IReadOnlyList<LocalCoderFileContext>> LoadAsync(
        string projectRoot,
        IReadOnlyList<string> relativePaths);

    Task<LocalCoderPresetPreview> PreviewPresetAsync(
        string projectRoot,
        string currentPagePath,
        LocalCoderContextPreset preset,
        IReadOnlyList<string> selectedPaths);

    Task<LocalCoderContextRestoreResult> RestoreAsync(
        string projectRoot,
        IReadOnlyList<LocalCoderHistoryContextFile> contextFiles);
}
