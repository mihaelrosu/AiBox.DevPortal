using System.Text;
using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSlicePatchPreviewPreparationService(
    ILocalCoderContextService localCoderContextService)
{
    public async Task<PromptContextPreparationResult> PreparePromptContextAsync(
        string taskText,
        string projectPath,
        bool includeNoMatchMessage = false,
        CancellationToken cancellationToken = default)
    {
        var debugDetails = new List<string>();
        var extraction = ExtractPromptFileTargets(taskText);
        var inferredCreateFolders = InferCreateFolders(taskText);

        debugDetails.Add($"PromptCreateTargets count: {extraction.CreateTargets.Count}");
        debugDetails.Add($"PromptModifyTargets count: {extraction.ModifyTargets.Count}");
        debugDetails.Add($"PromptContextTargets count: {extraction.ContextTargets.Count}");
        debugDetails.Add($"PromptInferredCreateFolders count: {inferredCreateFolders.Count}");

        if (extraction.CreateTargets.Count == 0
            && extraction.ModifyTargets.Count == 0
            && extraction.ContextTargets.Count == 0
            && inferredCreateFolders.Count == 0)
        {
            return new PromptContextPreparationResult(
                false,
                [],
                [],
                [],
                debugDetails,
                includeNoMatchMessage ? "No context files were detected from the task." : string.Empty);
        }

        var selectedFilePaths = extraction.ModifyTargets
            .Concat(extraction.ContextTargets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IReadOnlyList<LocalCoderFileContext> loadedContexts = selectedFilePaths.Length == 0
            ? []
            : await TryLoadFileContextsAsync(projectPath, selectedFilePaths, cancellationToken);

        var allowedCreateFolders = DeriveCreateFolders(extraction.CreateTargets)
            .Concat(inferredCreateFolders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var prepared = selectedFilePaths.Length > 0 || extraction.CreateTargets.Count > 0 || allowedCreateFolders.Length > 0;

        debugDetails.Add($"PromptSelectedFilePaths count: {selectedFilePaths.Length}");
        debugDetails.Add($"PromptLoadedContexts count: {loadedContexts.Count}");
        debugDetails.Add($"PromptAllowedCreateFolders count: {allowedCreateFolders.Length}");

        return new PromptContextPreparationResult(
            prepared,
            selectedFilePaths,
            loadedContexts,
            allowedCreateFolders,
            debugDetails,
            prepared ? "Context prepared from task prompt." : string.Empty);
    }

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

        if (sliceContexts.Count == 0 && createTargets.Count == 0 && normalizedTargetFiles.Count > 0)
        {
            createTargets = normalizedTargetFiles;
            debugDetails.Add("CreateTargets inferred from TargetFiles.");
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

    private static IReadOnlyList<string> DeriveCreateFolders(IEnumerable<string> filePaths)
    {
        return filePaths
            .Select(path => (path ?? string.Empty).Replace('\\', '/').Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path =>
            {
                var lastSeparator = path.LastIndexOf('/');
                return lastSeparator <= 0 ? string.Empty : $"{path[..lastSeparator]}/";
            })
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static PromptPathExtractionResult ExtractPromptFileTargets(string taskText)
    {
        var value = taskText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new PromptPathExtractionResult([], [], []);
        }

        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var createTargets = new List<string>();
        var modifyTargets = new List<string>();
        var contextTargets = new List<string>();
        PromptSection? currentSection = null;

        foreach (var rawLine in normalized.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                currentSection = null;
                continue;
            }

            if (TryConsumePromptSectionHeader(trimmed, out var section, out var remainder))
            {
                currentSection = section;
                AddPromptPaths(section, remainder, createTargets, modifyTargets, contextTargets);
                continue;
            }

            var sectionToUse = currentSection ?? InferPromptSection(trimmed);
            if (sectionToUse is null)
            {
                continue;
            }

            AddPromptPaths(sectionToUse.Value, trimmed, createTargets, modifyTargets, contextTargets);
        }

        return new PromptPathExtractionResult(
            createTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            modifyTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            contextTargets.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<string> InferCreateFolders(string taskText)
    {
        var value = (taskText ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var folders = new List<string>();

        foreach (var rawLine in value.Split('\n'))
        {
            var lowered = rawLine.ToLowerInvariant();
            if (!ContainsAny(lowered, "create", "new ", "add ", "scaffold", "generate", "implement"))
            {
                continue;
            }

            if (ContainsAny(lowered, "model", "models"))
            {
                folders.Add("Models/");
            }

            if (ContainsAny(lowered, "service", "services"))
            {
                folders.Add("Services/");
            }

            if (ContainsAny(lowered, "card", "component", "page", "razor", "ui"))
            {
                folders.Add("Components/Pages/");
            }
        }

        return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddPromptPaths(
        PromptSection section,
        string text,
        List<string> createTargets,
        List<string> modifyTargets,
        List<string> contextTargets)
    {
        foreach (Match match in PromptPathRegex().Matches(text ?? string.Empty))
        {
            var normalized = NormalizePromptPath(match.Groups["path"].Value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            switch (section)
            {
                case PromptSection.Create:
                    createTargets.Add(normalized);
                    break;
                case PromptSection.Modify:
                    modifyTargets.Add(normalized);
                    break;
                case PromptSection.Files:
                case PromptSection.Context:
                    contextTargets.Add(normalized);
                    break;
            }
        }
    }

    private static bool TryConsumePromptSectionHeader(string line, out PromptSection section, out string remainder)
    {
        var match = PromptSectionHeaderRegex().Match(line);
        if (!match.Success)
        {
            section = default;
            remainder = string.Empty;
            return false;
        }

        section = match.Groups["section"].Value.ToLowerInvariant() switch
        {
            "create" => PromptSection.Create,
            "modify" => PromptSection.Modify,
            "files" => PromptSection.Files,
            "context" => PromptSection.Context,
            _ => default
        };
        remainder = match.Groups["rest"].Value.Trim();
        return true;
    }

    private static PromptSection? InferPromptSection(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var hasPath = PromptPathRegex().IsMatch(line);
        if (!hasPath)
        {
            return null;
        }

        var lowered = line.ToLowerInvariant();
        if (ContainsAny(lowered, "create", "new ", "add ", "scaffold", "generate"))
        {
            return PromptSection.Create;
        }

        if (ContainsAny(lowered, "modify", "update", "change", "edit", "fix", "refactor", "patch", "implement", "remove", "delete", "rename", "replace", "adjust"))
        {
            return PromptSection.Modify;
        }

        return PromptSection.Context;
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePromptPath(string path)
    {
        return (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('-').Trim();
    }

    private static Regex PromptSectionHeaderRegex()
    {
        return new Regex(@"^\s*(?:#{1,6}\s*)?(?:[-*]\s*)?(?<section>create|modify|files|context)\s*:\s*(?<rest>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static Regex PromptPathRegex()
    {
        return new Regex(@"(?<path>(?:[\w.\-]+[\\/])*(?:[\w.\-]+\.(?:cs|razor|csproj|json|md)))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private enum PromptSection
    {
        Create,
        Modify,
        Files,
        Context
    }
}

public sealed record TaskSlicePatchPreviewPreparationResult(
    bool CanGenerate,
    string TaskText,
    IReadOnlyList<LocalCoderFileContext> FileContexts,
    IReadOnlyList<string> DebugDetails,
    string FailureMessage);

internal sealed record PromptPathExtractionResult(
    IReadOnlyList<string> CreateTargets,
    IReadOnlyList<string> ModifyTargets,
    IReadOnlyList<string> ContextTargets);

public sealed record PromptContextPreparationResult(
    bool Prepared,
    IReadOnlyList<string> SelectedFilePaths,
    IReadOnlyList<LocalCoderFileContext> FileContexts,
    IReadOnlyList<string> AllowedCreateFolders,
    IReadOnlyList<string> DebugDetails,
    string Message);
