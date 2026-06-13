using AiBox.DevPortal.Models;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class LocalCoderPatchPreviewMetricsTests
{
    [Fact]
    public void FromHistory_CountsAttemptsSuccessFailuresAndRepairs()
    {
        var metrics = LocalCoderPatchPreviewMetricsCalculator.FromHistory(
        [
            new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = true,
                RepairSummary = new PatchPreviewRepairSummary
                {
                    OriginalOperation = "replace",
                    RepairAttempt = "insert_before",
                    RepairResult = "Success"
                }
            },
            new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = true
            },
            new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                Success = false
            },
            new LocalCoderHistoryEntry
            {
                ActionType = LocalCoderHistoryActionType.CreatePlan,
                Success = true
            }
        ]);

        Assert.Equal(3, metrics.Attempts);
        Assert.Equal(2, metrics.SuccessfulPreviews);
        Assert.Equal(1, metrics.FailedPreviews);
        Assert.Equal(1, metrics.RepairedPreviews);
    }
}
