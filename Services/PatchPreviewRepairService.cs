using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public sealed class PatchPreviewRepairService(
    IOllamaService ollamaService,
    IPatchEditOperationService patchEditOperationService,
    IConfiguration configuration)
{
    public async Task<PatchPreviewRepairResult> RepairAsync(
        LocalCoderRequest request,
        PatchIntent intent,
        string intentText,
        string selectedFilePathsText,
        string fileContextText,
        string scopeText,
        bool xmlDocumentationMode,
        PatchPromptTargetResolution? targetResolution,
        PatchPreviewRepairContext repairContext,
        string agentInstructionsText,
        string originalOperation,
        string repairAttempt,
        AgentModeProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(repairContext);

        var prompt = BuildRepairPrompt(
            request,
            selectedFilePathsText,
            fileContextText,
            scopeText,
            intentText,
            xmlDocumentationMode,
            targetResolution,
            repairContext,
            profile,
            agentInstructionsText);

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? configuration["AiBox:LocalCoder:DefaultModel"] ?? "qwen2.5-coder:7b"
            : request.Model;

        try
        {
            var repairRawResponse = await ollamaService.GenerateAsync(model, prompt, cancellationToken);
            var repairNormalizedResponse = NormalizeModelResponse(repairRawResponse);

            if (xmlDocumentationMode)
            {
                CoderConsoleService.ValidateXmlDocumentationOperationModes(repairRawResponse);
            }

            var editResult = await patchEditOperationService.BuildAsync(
                request.ProjectPath,
                request.FileContexts,
                repairRawResponse,
                intent,
                cancellationToken);

            return new PatchPreviewRepairResult
            {
                Success = true,
                EditResult = editResult,
                RepairSummary = new PatchPreviewRepairSummary
                {
                    OriginalOperation = originalOperation,
                    RepairAttempt = editResult.Operations.FirstOrDefault()?.Operation ?? repairAttempt,
                    RepairResult = "Success",
                    ValidationError = string.Empty
                },
                OriginalValidationException = BuildOriginalValidationException(repairContext),
                RepairPrompt = prompt,
                OriginalRawResponse = repairContext.RawModelResponse,
                OriginalNormalizedResponse = NormalizeModelResponse(repairContext.RawModelResponse),
                RepairRawResponse = repairRawResponse,
                RepairNormalizedResponse = repairNormalizedResponse,
                CombinedValidationErrors = []
            };
        }
        catch (PatchPreviewValidationException repairValidationException)
        {
            return BuildFailureResult(
                prompt,
                repairContext,
                repairValidationException,
                originalOperation,
                repairAttempt,
                repairValidationException.RawModelResponse,
                repairValidationException.NormalizedResponse,
                repairValidationException.ValidationErrors);
        }
        catch (Exception exception)
        {
            var repairValidationException = new PatchPreviewValidationException(
                $"Patch preview validation failed:{Environment.NewLine}- {exception.Message}",
                [exception.Message],
                string.Empty,
                string.Empty,
                string.Empty);

            return BuildFailureResult(
                prompt,
                repairContext,
                repairValidationException,
                originalOperation,
                repairAttempt,
                string.Empty,
                string.Empty,
                [exception.Message]);
        }
    }

    private static PatchPreviewRepairResult BuildFailureResult(
        string prompt,
        PatchPreviewRepairContext repairContext,
        PatchPreviewValidationException repairValidationException,
        string originalOperation,
        string repairAttempt,
        string repairRawResponse,
        string repairNormalizedResponse,
        IReadOnlyList<string> repairErrors)
    {
        var originalValidationException = BuildOriginalValidationException(repairContext);
        var combinedErrors = originalValidationException.ValidationErrors
            .Concat(repairErrors)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var failureMessage = $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", combinedErrors)}";
        var combinedException = new PatchPreviewValidationException(
            failureMessage,
            combinedErrors,
            repairRawResponse,
            string.Empty,
            repairNormalizedResponse,
            repairValidationException.OperationGrammarErrors,
            repairValidationException.Guidance,
            repairValidationException.ReplaceDiagnostics,
            repairValidationException.SuggestedTargetDiagnostics);

        combinedException.RepairSummary = new PatchPreviewRepairSummary
        {
            OriginalOperation = originalOperation,
            RepairAttempt = repairAttempt,
            RepairResult = "Failed",
            ValidationError = failureMessage
        };

        return new PatchPreviewRepairResult
        {
            Success = false,
            RepairSummary = combinedException.RepairSummary,
            OriginalValidationException = originalValidationException,
            RepairValidationException = repairValidationException,
            ValidationException = combinedException,
            RepairPrompt = prompt,
            OriginalRawResponse = repairContext.RawModelResponse,
            OriginalNormalizedResponse = NormalizeModelResponse(repairContext.RawModelResponse),
            RepairRawResponse = repairRawResponse,
            RepairNormalizedResponse = repairNormalizedResponse,
            CombinedValidationErrors = combinedErrors,
            FailureMessage = failureMessage
        };
    }

    private static PatchPreviewValidationException BuildOriginalValidationException(PatchPreviewRepairContext repairContext)
    {
        return new PatchPreviewValidationException(
            $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", repairContext.ValidationErrors)}",
            repairContext.ValidationErrors.ToArray(),
            repairContext.RawModelResponse,
            string.Empty,
            string.Empty,
            replaceDiagnostics: repairContext.ReplaceDiagnostics.ToArray(),
            suggestedTargetDiagnostics: repairContext.SuggestedTargetDiagnostics.ToArray());
    }

    private static string BuildRepairPrompt(
        LocalCoderRequest request,
        string selectedFilePathsText,
        string fileContextText,
        string scopeText,
        string intentText,
        bool xmlDocumentationMode,
        PatchPromptTargetResolution? targetResolution,
        PatchPreviewRepairContext repairContext,
        AgentModeProfile? profile,
        string agentInstructionsText)
    {
        var basePrompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            selectedFilePathsText,
            fileContextText,
            scopeText,
            intentText,
            xmlDocumentationMode,
            targetResolution,
            repairContext,
            profile,
            agentInstructionsText);

        return $"""
        Repair constraints:
        - Do not invent new changes.
        - Do not invent new files, operations, or targets.
        - Only fix JSON shape, operation names, missing required fields, or anchors using existing context.
        - XML documentation comments must end with a newline before the member declaration.
        - Return corrected JSON only.

        Allowed files:
        {selectedFilePathsText}

        {basePrompt}
        """;
    }

    internal static string NormalizeModelResponse(string? rawResponse)
    {
        var trimmed = (rawResponse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        var fencedBlock = TryExtractMarkdownFence(trimmed);
        return !string.IsNullOrWhiteSpace(fencedBlock)
            ? fencedBlock
            : TryExtractJsonSubstring(trimmed) ?? trimmed;
    }

    internal static IReadOnlyList<string> GetJsonParseCandidates(string? rawResponse)
    {
        var trimmed = (rawResponse ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [trimmed];
        }

        var candidates = new List<string> { trimmed };
        AddCandidate(candidates, TryExtractMarkdownFence(trimmed));
        AddCandidate(candidates, TryExtractJsonSubstring(trimmed));
        return candidates;
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate) &&
            !candidates.Contains(candidate, StringComparer.Ordinal))
        {
            candidates.Add(candidate);
        }
    }

    private static string? TryExtractMarkdownFence(string text)
    {
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart < 0)
        {
            return null;
        }

        var openingLineEnd = text.IndexOf('\n', fenceStart);
        if (openingLineEnd < 0)
        {
            return null;
        }

        var openingFence = text[fenceStart..openingLineEnd].TrimEnd('\r');
        if (!openingFence.Equals("```", StringComparison.Ordinal) &&
            !openingFence.Equals("```json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var closingFenceStart = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceStart <= openingLineEnd)
        {
            return null;
        }

        var closingLineStart = text.LastIndexOf('\n', closingFenceStart);
        if (closingLineStart < 0)
        {
            return null;
        }

        var closingLineEnd = text.IndexOf('\n', closingFenceStart);
        closingLineEnd = closingLineEnd < 0 ? text.Length : closingLineEnd;

        var closingFence = text[(closingLineStart + 1)..closingLineEnd].TrimEnd('\r').Trim();
        if (!closingFence.Equals("```", StringComparison.Ordinal))
        {
            return null;
        }

        return text[(openingLineEnd + 1)..closingLineStart].Trim();
    }

    internal static string? TryExtractJsonSubstring(string text)
    {
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');

        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return null;
        }

        return text[firstBrace..(lastBrace + 1)].Trim();
    }
}
