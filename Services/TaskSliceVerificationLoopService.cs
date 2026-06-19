using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceVerificationLoopService(
    TaskSliceVerificationService verificationService,
    TaskSliceRiskAnalysisService riskAnalysisService)
{
    public const int MaxAttempts = 3;

    public async Task<TaskSliceVerificationLoopResult> VerifyAsync(
        TaskPlanSlice slice,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        if (!TaskSliceVerificationService.CanVerify(slice))
        {
            return new TaskSliceVerificationLoopResult
            {
                Success = false,
                Attempts = 0,
                VerificationResults = [],
                FinalMessage = $"Slice '{slice.Title}' must be in Previewed status before verification can start."
            };
        }

        var verificationResults = new List<TaskSliceExecutionResult>();
        var currentSlice = slice;
        TaskPlanSlice? generatedFixSlice = null;
        TaskSliceRiskAnalysis? finalRiskAnalysis = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var riskAnalysis = riskAnalysisService.Analyze(currentSlice);
            finalRiskAnalysis = riskAnalysis;

            if (riskAnalysis.RiskLevel == TaskSliceRiskLevel.Critical)
            {
                return BuildResult(
                    success: false,
                    verificationResults,
                    generatedFixSlice,
                    finalRiskAnalysis,
                    $"Verification loop stopped before attempt {attempt}/{MaxAttempts} because '{currentSlice.Title}' is critical risk.");
            }

            var verificationResult = await verificationService.VerifySliceAsync(currentSlice, cancellationToken);
            verificationResults.Add(Clone(verificationResult));

            if (verificationResult.Success)
            {
                return BuildResult(
                    success: true,
                    verificationResults,
                    generatedFixSlice,
                    finalRiskAnalysis,
                    $"Verification succeeded on attempt {attempt}/{MaxAttempts}.");
            }

            if (attempt == MaxAttempts)
            {
                return BuildResult(
                    success: false,
                    verificationResults,
                    generatedFixSlice,
                    finalRiskAnalysis,
                    $"Verification failed after {MaxAttempts} attempts.");
            }

            generatedFixSlice = CreateRemediationSlice(currentSlice, verificationResult, attempt + 1);
            currentSlice = generatedFixSlice;
        }

        return BuildResult(
            success: false,
            verificationResults,
            generatedFixSlice,
            finalRiskAnalysis,
            $"Verification failed after {MaxAttempts} attempts.");
    }

    private static TaskSliceVerificationLoopResult BuildResult(
        bool success,
        List<TaskSliceExecutionResult> verificationResults,
        TaskPlanSlice? generatedFixSlice,
        TaskSliceRiskAnalysis? finalRiskAnalysis,
        string message)
    {
        return new TaskSliceVerificationLoopResult
        {
            Success = success,
            Attempts = verificationResults.Count,
            VerificationResults = verificationResults.Select(Clone).ToArray(),
            FinalMessage = message,
            GeneratedFixSlice = generatedFixSlice,
            FinalRiskAnalysis = finalRiskAnalysis
        };
    }

    private static TaskSliceExecutionResult Clone(TaskSliceExecutionResult result)
    {
        return new TaskSliceExecutionResult
        {
            PlanId = result.PlanId,
            SliceId = result.SliceId,
            SliceTitle = result.SliceTitle,
            RequestedAction = result.RequestedAction,
            PatchPackageId = result.PatchPackageId,
            BackupId = result.BackupId,
            Success = result.Success,
            BuildSuccess = result.BuildSuccess,
            VerificationSuccess = result.VerificationSuccess,
            AppliedFiles = [.. result.AppliedFiles],
            AppliedAt = result.AppliedAt,
            Summary = result.Summary,
            GeneratedFiles = [.. result.GeneratedFiles],
            Errors = [.. result.Errors],
            ExecutedAt = result.ExecutedAt
        };
    }

    private static TaskPlanSlice CreateRemediationSlice(
        TaskPlanSlice sourceSlice,
        TaskSliceExecutionResult failedResult,
        int nextAttempt)
    {
        var now = DateTime.UtcNow;
        var remediationRequest = BuildRemediationRequest(sourceSlice, failedResult, nextAttempt);

        return new TaskPlanSlice
        {
            Id = Guid.NewGuid().ToString("N"),
            PlanId = sourceSlice.PlanId,
            PatchPackageId = string.Empty,
            Title = $"{sourceSlice.Title} - Remediation {nextAttempt}",
            Goal = remediationRequest,
            Description = remediationRequest,
            Status = TaskSliceStatus.Previewed,
            PatchPreviewCreatedAt = now,
            TargetFiles = [.. sourceSlice.TargetFiles],
            InstructionFiles = [.. sourceSlice.InstructionFiles],
            AllowedChangeType = sourceSlice.AllowedChangeType,
            MustNotChange = [.. sourceSlice.MustNotChange],
            VerificationCommands = sourceSlice.VerificationCommands.Count > 0 ? [.. sourceSlice.VerificationCommands] : ["dotnet build"],
            RelatedFiles = [.. (sourceSlice.RelatedFiles.Count > 0 ? sourceSlice.RelatedFiles : sourceSlice.TargetFiles)],
            Notes = AppendNotes(sourceSlice.Notes, remediationRequest),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string BuildRemediationRequest(
        TaskPlanSlice sourceSlice,
        TaskSliceExecutionResult failedResult,
        int nextAttempt)
    {
        var builder = new StringBuilder();
        builder.Append($"Remediate verification failure from '{sourceSlice.Title}' for attempt {nextAttempt}/{MaxAttempts}.");

        if (!string.IsNullOrWhiteSpace(failedResult.Summary))
        {
            builder.Append(' ').Append(failedResult.Summary.Trim());
        }

        if (failedResult.Errors.Count > 0)
        {
            builder.Append(" Errors:");
            foreach (var error in failedResult.Errors)
            {
                builder.Append(' ').Append(error.Trim());
            }
        }

        return builder.ToString();
    }

    private static string AppendNotes(string notes, string remediationRequest)
    {
        return string.IsNullOrWhiteSpace(notes)
            ? remediationRequest
            : $"{notes.Trim()}{Environment.NewLine}{remediationRequest}";
    }
}
