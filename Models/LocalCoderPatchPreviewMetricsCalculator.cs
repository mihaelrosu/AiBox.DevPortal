namespace AiBox.DevPortal.Models;

public static class LocalCoderPatchPreviewMetricsCalculator
{
    public static PatchPreviewMetricsSnapshot FromHistory(IReadOnlyList<LocalCoderHistoryEntry> historyEntries)
    {
        var previewEntries = historyEntries
            .Where(entry => entry.ActionType == LocalCoderHistoryActionType.GeneratePatchPreview)
            .ToArray();

        return new PatchPreviewMetricsSnapshot
        {
            Attempts = previewEntries.Length,
            SuccessfulPreviews = previewEntries.Count(entry => entry.Success),
            FailedPreviews = previewEntries.Count(entry => !entry.Success),
            RepairedPreviews = previewEntries.Count(entry => entry.Success && entry.RepairSummary is not null)
        };
    }
}
