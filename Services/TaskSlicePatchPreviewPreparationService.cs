using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSlicePatchPreviewPreparationService(
    ILocalCoderContextService localCoderContextService)
{
    public async Task<TaskSlicePatchPreviewPreparationResult> PrepareAsync(
        TaskPlanSlice slice,
        string projectPath,
        IReadOnlyList<LocalCoderFileContext>? selectedContextFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var debugDetails = new List<string>();
        var targetFileInputs = slice.TargetFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var relatedFileInputs = slice.RelatedFiles.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var normalizedTargetFiles = NormalizeSlicePaths(targetFileInputs);
        var normalizedRelatedFiles = NormalizeSlicePaths(relatedFileInputs);
        var normalizedSelectedContexts = (selectedContextFiles ?? [])
            .Where(context => context is not null && !string.IsNullOrWhiteSpace(context.RelativePath))
            .DistinctBy(context => context.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedContextPaths = normalizedSelectedContexts
            .Select(context => context.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        debugDetails.Add($"SliceId: {slice.Id}");
        debugDetails.Add($"SliceTitle: {slice.Title}");
        debugDetails.Add($"TargetFiles count: {normalizedTargetFiles.Count}");
        debugDetails.Add($"RelatedFiles count: {normalizedRelatedFiles.Count}");
        debugDetails.Add($"SelectedContextFiles count: {selectedContextPaths.Length}");

        if (ContainsInvalidSlicePath(targetFileInputs))
        {
            return Fail(slice, "Slice TargetFiles contain invalid paths.", debugDetails);
        }

        if (ContainsInvalidSlicePath(relatedFileInputs))
        {
            return Fail(slice, "Slice RelatedFiles contain invalid paths.", debugDetails);
        }

        if (string.IsNullOrWhiteSpace(slice.Id) || string.IsNullOrWhiteSpace(slice.Title))
        {
            return Fail(slice, "Slice id and title are required.", debugDetails);
        }

        var baseTaskText = BuildSlicePatchTaskText(slice, normalizedTargetFiles, normalizedRelatedFiles);
        var createTargets = NormalizeSlicePaths(PatchIntentService.ExtractRequestedCreateFiles(baseTaskText));
        debugDetails.Add($"CreateTargets count: {createTargets.Count}");

        if (normalizedTargetFiles.Count == 0
            && normalizedRelatedFiles.Count == 0
            && selectedContextPaths.Length == 0
            && createTargets.Count == 0)
        {
            return Fail(slice, SlicePreviewMissingTargetsMessage, debugDetails);
        }

        var selectedSource = string.Empty;
        IReadOnlyList<LocalCoderFileContext> sliceContexts = [];

        if (normalizedTargetFiles.Count > 0)
        {
            try
            {
                var loadedContexts = await TryLoadFileContextsAsync(projectPath, normalizedTargetFiles, cancellationToken);
                if (loadedContexts.Count > 0)
                {
                    selectedSource = "TargetFiles";
                    sliceContexts = loadedContexts;
                }
                else
                {
                    debugDetails.Add("TargetFiles did not resolve to any loadable files.");
                }
            }
            catch (Exception exception)
            {
                debugDetails.Add($"TargetFiles load failed: {exception.Message}");
            }
        }

        if (sliceContexts.Count == 0 && normalizedRelatedFiles.Count > 0)
        {
            try
            {
                var loadedContexts = await TryLoadFileContextsAsync(projectPath, normalizedRelatedFiles, cancellationToken);
                if (loadedContexts.Count > 0)
                {
                    selectedSource = "RelatedFiles";
                    sliceContexts = loadedContexts;
                }
                else
                {
                    debugDetails.Add("RelatedFiles did not resolve to any loadable files.");
                }
            }
            catch (Exception exception)
            {
                debugDetails.Add($"RelatedFiles load failed: {exception.Message}");
            }
        }

        if (sliceContexts.Count == 0 && normalizedSelectedContexts.Length > 0)
        {
            selectedSource = "SelectedContextFiles";
            sliceContexts = normalizedSelectedContexts;
        }

        if (sliceContexts.Count == 0 && createTargets.Count > 0)
        {
            selectedSource = "CreateTargets";
            sliceContexts = createTargets
                .Select(path => new LocalCoderFileContext
                {
                    RelativePath = path,
                    FullPath = string.IsNullOrWhiteSpace(projectPath) ? path : Path.Combine(projectPath, path),
                    Content = string.Empty,
                    IsTruncated = false,
                    IsGeneratedFile = false
                })
                .ToArray();
        }

        if (sliceContexts.Count == 0)
        {
            return Fail(slice, SlicePreviewMissingTargetsMessage, debugDetails);
        }

        debugDetails.Add($"SelectedContextSource: {selectedSource}");
        debugDetails.Add($"SelectedContextFiles used: {sliceContexts.Count}");

        var taskText = BuildSlicePatchTaskText(slice, normalizedTargetFiles, normalizedRelatedFiles, createTargets);
        return new TaskSlicePatchPreviewPreparationResult(
            true,
            taskText,
            sliceContexts,
            debugDetails,
            string.Empty);
    }

    internal static string BuildSlicePatchTaskText(
        TaskPlanSlice slice,
        IReadOnlyList<string>? targetFiles = null,
        IReadOnlyList<string>? relatedFiles = null,
        IReadOnlyList<string>? createTargets = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine(slice.Title);

        var description = !string.IsNullOrWhiteSpace(slice.Description) ? slice.Description : slice.Goal;
        var effectiveTargetFiles = targetFiles ?? slice.TargetFiles;
        var effectiveRelatedFiles = relatedFiles ?? [];

        if (!string.IsNullOrWhiteSpace(description))
        {
            builder.AppendLine(description);
        }

        if (effectiveTargetFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Target files:");
            foreach (var targetFile in effectiveTargetFiles)
            {
                builder.AppendLine($"- {targetFile}");
            }
        }

        if (slice.InstructionFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Instruction files:");
            foreach (var instructionFile in slice.InstructionFiles)
            {
                builder.AppendLine($"- {instructionFile}");
            }
        }

        if (slice.VerificationCommands.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Verification commands:");
            foreach (var verificationCommand in slice.VerificationCommands)
            {
                builder.AppendLine($"- {verificationCommand}");
            }
        }

        if (effectiveRelatedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Related files:");
            foreach (var relatedFile in effectiveRelatedFiles)
            {
                builder.AppendLine($"- {relatedFile}");
            }
        }

        if (createTargets is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("Create targets:");
            foreach (var createTarget in createTargets)
            {
                builder.AppendLine($"- {createTarget}");
            }
        }

        if (!string.IsNullOrWhiteSpace(slice.Notes))
        {
            builder.AppendLine();
            builder.AppendLine("Notes:");
            builder.AppendLine(slice.Notes);
        }

        return builder.ToString().Trim();
    }

    private static TaskSlicePatchPreviewPreparationResult Fail(
        TaskPlanSlice slice,
        string message,
        List<string> debugDetails)
    {
        return new TaskSlicePatchPreviewPreparationResult(
            false,
            string.Empty,
            [],
            debugDetails,
            $"Slice '{slice.Title}' failed patch preview preparation: {message}");
    }

    private async Task<IReadOnlyList<LocalCoderFileContext>> TryLoadFileContextsAsync(
        string projectPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || relativePaths.Count == 0)
        {
            return [];
        }

        var existingPaths = relativePaths
            .Where(path => File.Exists(Path.Combine(projectPath, path)))
            .ToArray();

        if (existingPaths.Length == 0)
        {
            return [];
        }

        return await localCoderContextService.LoadAsync(projectPath, existingPaths);
    }

    private static List<string> NormalizeSlicePaths(IEnumerable<string> paths)
    {
        return paths
            .Select(path => (path ?? string.Empty).Replace('\\', '/').Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => !Path.IsPathRooted(path) && !path.Contains("..", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsInvalidSlicePath(IEnumerable<string> paths)
    {
        return paths.Any(path =>
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
            return string.IsNullOrWhiteSpace(normalized)
                   || Path.IsPathRooted(normalized)
                   || normalized.Contains("..", StringComparison.Ordinal);
        });
    }

    private const string SlicePreviewMissingTargetsMessage =
        "This slice has no valid target files, related files, selected context files, or create targets. Add slice targets before generating a patch preview.";
}

public sealed record TaskSlicePatchPreviewPreparationResult(
    bool CanGenerate,
    string TaskText,
    IReadOnlyList<LocalCoderFileContext> FileContexts,
    IReadOnlyList<string> DebugDetails,
    string FailureMessage);
