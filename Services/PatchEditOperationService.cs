using System.Diagnostics;
using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchEditOperationService(ILogger<PatchEditOperationService> logger) : IPatchEditOperationService
{
    private readonly ILogger<PatchEditOperationService> logger = logger;
    private const int MaxRemovalCharacters = 10 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "create",
        "insert_after",
        "insert_before",
        "replace",
        "remove"
    };
    private static readonly HashSet<string> BinaryAndMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".class", ".dll", ".doc", ".docx", ".exe", ".flac",
        ".gif", ".gz", ".ico", ".jar", ".jpeg", ".jpg", ".mkv", ".mov", ".mp3", ".mp4",
        ".pdf", ".png", ".ppt", ".pptx", ".rar", ".so", ".svg", ".tar", ".tiff", ".wav",
        ".webm", ".webp", ".xls", ".xlsx", ".zip"
    };
    private static readonly string[] RazorDirectives = ["@page", "@using", "@inject", "@attribute", "@layout"];

    public async Task<PatchEditOperationResult> BuildAsync(
        string projectPath,
        IReadOnlyList<LocalCoderFileContext> selectedFileContexts,
        string rawJson,
        CancellationToken cancellationToken = default)
    {
        var rootPath = ValidateProjectRoot(projectPath);
        var response = ParseResponse(rawJson);
        if (response.Operations.Count == 0 && response.Errors.Count > 0)
        {
            throw new PatchPreviewValidationException(
                "The model returned an invalid patch operation.",
                response.Errors.ToArray(),
                rawJson ?? string.Empty,
                string.Empty,
                string.Empty,
                response.Errors.ToArray());
        }

        var selectedContextMap = selectedFileContexts
            .ToDictionary(context => NormalizeRelativePath(context.RelativePath), context => context.Content, StringComparer.OrdinalIgnoreCase);

        if (selectedContextMap.Count == 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                ["Patch preview requires selected file context."],
                response.Operations,
                []);
        }

        var fileStateMap = selectedContextMap.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        var originalContentMap = selectedContextMap.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);

        var validationErrors = new List<string>();
        var replaceDiagnostics = new List<PatchReplaceDiagnostic>();
        var suggestedTargetDiagnostics = new List<PatchSuggestedTargetDiagnostic>();

        if (response.Operations.Count == 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                ["Patch preview operations list is empty."],
                response.Operations,
                replaceDiagnostics);
        }

        foreach (var operation in response.Operations)
        {
            var normalizedOperation = operation.Operation.Trim();
            if (!AllowedOperations.Contains(normalizedOperation))
            {
                validationErrors.Add($"Unsupported operation '{operation.Operation}' for file '{operation.FilePath}'.");
                continue;
            }

            var filePath = NormalizeRelativePath(operation.FilePath);
            var isCreate = normalizedOperation.Equals("create", StringComparison.OrdinalIgnoreCase);

            if (!ValidatePath(rootPath, filePath, selectedContextMap, isCreate, validationErrors))
            {
                continue;
            }

            var currentPath = Path.Combine(rootPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            var fileExists = File.Exists(currentPath);
            if (isCreate)
            {
                if (fileExists)
                {
                    validationErrors.Add($"Create operation target file already exists: {filePath}");
                    continue;
                }

                fileStateMap[filePath] = string.Empty;
                originalContentMap[filePath] = string.Empty;
            }
            else if (!fileExists)
            {
                validationErrors.Add($"Patch edit target file does not exist: {filePath}");
                continue;
            }

            if (!fileStateMap.TryGetValue(filePath, out var currentContent))
            {
                currentContent = originalContentMap.TryGetValue(filePath, out var originalContent)
                    ? originalContent
                    : string.Empty;
            }

            var result = ApplyOperation(operation, filePath, currentContent, validationErrors, replaceDiagnostics, suggestedTargetDiagnostics);
            if (result is null)
            {
                continue;
            }

            fileStateMap[filePath] = result;
        }

        if (validationErrors.Count > 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                validationErrors,
                response.Operations,
                replaceDiagnostics,
                suggestedTargetDiagnostics);
        }

        var fileChanges = fileStateMap
            .Where(pair => !string.Equals(pair.Value, originalContentMap[pair.Key], StringComparison.Ordinal))
            .Select(pair => new PatchFileChange
            {
                RelativePath = pair.Key,
                OldContent = originalContentMap[pair.Key],
                NewContent = pair.Value
            })
            .ToArray();

        if (fileChanges.Length == 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                ["Patch edit operations produced no changes."],
                response.Operations,
                replaceDiagnostics,
                suggestedTargetDiagnostics);
        }

        var patchText = await BuildPatchTextAsync(fileChanges, cancellationToken);

        return new PatchEditOperationResult
        {
            Operations = response.Operations,
            FileChanges = fileChanges,
            PatchText = patchText
        };
    }

    private string? ApplyOperation(
        PatchEditOperation operation,
        string filePath,
        string currentContent,
        List<string> validationErrors,
        List<PatchReplaceDiagnostic> replaceDiagnostics,
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        var normalizedOperation = operation.Operation.Trim();
        var anchor = operation.Anchor ?? string.Empty;
        var oldText = operation.OldText ?? string.Empty;
        var newText = operation.NewText ?? string.Empty;

        if (normalizedOperation.Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(newText))
            {
                validationErrors.Add($"Operation 'create' for file '{filePath}' must include non-empty newText.");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(oldText))
            {
                validationErrors.Add($"Operation 'create' for file '{filePath}' must not include oldText.");
                return null;
            }

            return newText;
        }

        if (normalizedOperation.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            return RemoveText(filePath, currentContent, anchor, oldText, newText, validationErrors, suggestedTargetDiagnostics);
        }

        if (string.IsNullOrWhiteSpace(newText))
        {
            validationErrors.Add($"Operation '{normalizedOperation}' for file '{filePath}' must include non-empty newText.");
            return null;
        }

        return normalizedOperation.ToLowerInvariant() switch
        {
            "insert_before" => InsertBefore(filePath, currentContent, anchor, newText, validationErrors, suggestedTargetDiagnostics),
            "insert_after" => InsertAfter(filePath, currentContent, anchor, newText, validationErrors, suggestedTargetDiagnostics),
            "replace" => ReplaceText(filePath, currentContent, oldText, newText, validationErrors, replaceDiagnostics, suggestedTargetDiagnostics),
            _ => null
        };
    }

    private string? InsertBefore(
        string filePath,
        string currentContent,
        string anchor,
        string newText,
        List<string> validationErrors,
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            validationErrors.Add($"Operation 'insert_before' for file '{filePath}' requires a non-empty anchor.");
            return null;
        }

        if (!TryResolveAnchor("insert_before", filePath, currentContent, anchor, out var match))
        {
            AddSuggestedTargetDiagnostics(suggestedTargetDiagnostics, filePath, currentContent, "insert_before", "anchor", anchor);
            validationErrors.Add($"Anchor not found for insert_before in file '{filePath}': {anchor}");
            return null;
        }

        return currentContent[..match!.Index] + newText + currentContent[match.Index..];
    }

    private string? InsertAfter(
        string filePath,
        string currentContent,
        string anchor,
        string newText,
        List<string> validationErrors,
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            validationErrors.Add($"Operation 'insert_after' for file '{filePath}' requires a non-empty anchor.");
            return null;
        }

        if (!TryResolveAnchor("insert_after", filePath, currentContent, anchor, out var match))
        {
            AddSuggestedTargetDiagnostics(suggestedTargetDiagnostics, filePath, currentContent, "insert_after", "anchor", anchor);
            validationErrors.Add($"Anchor not found for insert_after in file '{filePath}': {anchor}");
            return null;
        }

        var insertIndex = match!.Index + match.Length;
        if (TryBuildRazorDirectiveInsertion(currentContent, match, newText, out insertIndex, out var directiveInsertion))
        {
            return currentContent[..insertIndex] + directiveInsertion + currentContent[insertIndex..];
        }

        return currentContent[..insertIndex] + newText + currentContent[insertIndex..];
    }

    private static bool TryBuildRazorDirectiveInsertion(
        string currentContent,
        PatchAnchorMatch match,
        string newText,
        out int insertIndex,
        out string insertion)
    {
        insertIndex = match.Index + match.Length;
        insertion = string.Empty;

        var lineStart = match.Index == 0
            ? -1
            : currentContent.LastIndexOf('\n', match.Index - 1);
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var lineContentEnd = currentContent.IndexOfAny(['\r', '\n'], match.Index + match.Length);
        lineContentEnd = lineContentEnd < 0 ? currentContent.Length : lineContentEnd;

        var directiveLine = currentContent[lineStart..lineContentEnd].TrimStart();
        if (!IsRazorDirectiveLine(directiveLine))
        {
            return false;
        }

        var lineEnding = DetectLineEnding(currentContent);
        var lineEnd = lineContentEnd;
        if (lineEnd < currentContent.Length)
        {
            lineEnd += currentContent[lineEnd] == '\r' &&
                       lineEnd + 1 < currentContent.Length &&
                       currentContent[lineEnd + 1] == '\n'
                ? 2
                : 1;
        }

        var normalizedNewText = newText
            .ReplaceLineEndings(lineEnding)
            .Trim('\r', '\n');

        var separatorBefore = lineEnd > lineContentEnd
            ? lineEnding
            : lineEnding + lineEnding;
        var separatorAfter = lineEnd < currentContent.Length
            ? lineEnding
            : string.Empty;

        insertIndex = lineEnd;
        insertion = separatorBefore + normalizedNewText + separatorAfter;
        return true;
    }

    private static bool IsRazorDirectiveLine(string line)
    {
        return RazorDirectives.Any(directive =>
            line.Equals(directive, StringComparison.Ordinal) ||
            (line.StartsWith(directive, StringComparison.Ordinal) &&
             line.Length > directive.Length &&
             char.IsWhiteSpace(line[directive.Length])));
    }

    private static string DetectLineEnding(string content)
    {
        if (content.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        if (content.Contains('\n'))
        {
            return "\n";
        }

        if (content.Contains('\r'))
        {
            return "\r";
        }

        return Environment.NewLine;
    }

    private bool TryResolveAnchor(
        string operation,
        string filePath,
        string currentContent,
        string anchor,
        out PatchAnchorMatch? match)
    {
        const string diagnosticText = "Patch Queue";
        var diagnosticIndex = currentContent.IndexOf(diagnosticText, StringComparison.Ordinal);
        var diagnosticCount = CountOccurrences(currentContent, diagnosticText);
        var diagnosticSnippet = BuildDiagnosticSnippet(currentContent, diagnosticIndex, 300);

        logger.LogInformation(
            "Resolving patch anchor. FilePath: {FilePath}; Operation: {Operation}; Anchor: {Anchor}; Contains Patch Queue: {ContainsPatchQueue}; Patch Queue count: {PatchQueueCount}; Patch Queue context: {PatchQueueContext}",
            filePath,
            operation,
            anchor,
            diagnosticIndex >= 0,
            diagnosticCount,
            diagnosticSnippet);

        if (!PatchAnchorMatcher.TryResolve(currentContent, anchor, out match))
        {
            return false;
        }

        logger.LogInformation(
            "Patch anchor matched for {Operation} in {FilePath}. Original anchor: {OriginalAnchor}; Normalized anchor: {NormalizedAnchor}; Match strategy: {MatchStrategy}",
            operation,
            filePath,
            match!.OriginalAnchor,
            match.NormalizedAnchor,
            match.Strategy);

        return true;
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var searchIndex = 0;

        while ((searchIndex = content.IndexOf(value, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            searchIndex += value.Length;
        }

        return count;
    }

    private static bool ContainsWildcardToken(string value)
    {
        return value.Contains('*') || value.Contains('?');
    }

    private static string BuildDiagnosticSnippet(string content, int matchIndex, int maxLength)
    {
        if (matchIndex < 0 || string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var startIndex = Math.Max(0, matchIndex - (maxLength / 2));
        var length = Math.Min(maxLength, content.Length - startIndex);
        return content.Substring(startIndex, length);
    }

    private static string? ReplaceText(
        string filePath,
        string currentContent,
        string oldText,
        string newText,
        List<string> validationErrors,
        List<PatchReplaceDiagnostic> replaceDiagnostics,
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(oldText))
        {
            validationErrors.Add($"Operation 'replace' for file '{filePath}' requires a non-empty oldText.");
            return null;
        }

        var index = currentContent.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            AddReplaceDiagnostics(replaceDiagnostics, filePath, currentContent, oldText);
            AddSuggestedTargetDiagnostics(suggestedTargetDiagnostics, filePath, currentContent, "replace", "oldText", oldText);
            validationErrors.Add($"Old text not found for replace in file '{filePath}': {oldText}");
            return null;
        }

        return currentContent[..index] + newText + currentContent[(index + oldText.Length)..];
    }

    private string? RemoveText(
        string filePath,
        string currentContent,
        string anchor,
        string oldText,
        string newText,
        List<string> validationErrors,
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics)
    {
        if (string.IsNullOrWhiteSpace(oldText))
        {
            validationErrors.Add($"Operation 'remove' for file '{filePath}' requires a non-empty oldText.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(newText))
        {
            validationErrors.Add($"Operation 'remove' for file '{filePath}' must include an empty newText.");
            return null;
        }

        if (IsBinaryOrMediaPath(filePath))
        {
            validationErrors.Add($"Remove operation for file '{filePath}' is blocked because binary and media files are not allowed.");
            return null;
        }

        if (oldText.Length > MaxRemovalCharacters)
        {
            validationErrors.Add($"Remove operation for file '{filePath}' exceeds the {MaxRemovalCharacters / 1024} KB safety limit.");
            return null;
        }

        var occurrenceCount = CountOccurrences(currentContent, oldText);
        if (occurrenceCount == 0)
        {
            AddSuggestedTargetDiagnostics(suggestedTargetDiagnostics, filePath, currentContent, "remove", "oldText", oldText);
            validationErrors.Add(
                ContainsWildcardToken(oldText)
                    ? $"Remove operation does not support wildcard matching in file '{filePath}': {oldText}"
                    : $"Exact text not found for remove in file '{filePath}': {oldText}");
            return null;
        }

        if (occurrenceCount > 1)
        {
            if (!TryResolveRemoveOccurrenceIndex(currentContent, oldText, anchor, out var occurrenceIndex))
            {
                validationErrors.Add($"Text appears multiple times; make the remove task more specific or provide a unique anchor in file '{filePath}': {oldText}");
                return null;
            }

            logger.LogInformation(
                "Patch remove operation resolved for {FilePath}. Operation: Remove; Removed text length: {RemovedTextLength}; Match count: {MatchCount}; Anchor provided: {AnchorProvided}",
                filePath,
                oldText.Length,
                occurrenceCount,
                !string.IsNullOrWhiteSpace(anchor));

            if (TryRemoveStandaloneLine(currentContent, oldText, occurrenceIndex, out var anchoredLineRemovedContent))
            {
                return anchoredLineRemovedContent;
            }

            return currentContent[..occurrenceIndex] + string.Empty + currentContent[(occurrenceIndex + oldText.Length)..];
        }

        logger.LogInformation(
            "Patch remove operation resolved for {FilePath}. Operation: Remove; Removed text length: {RemovedTextLength}; Match count: {MatchCount}; Anchor provided: {AnchorProvided}",
            filePath,
            oldText.Length,
            occurrenceCount,
            !string.IsNullOrWhiteSpace(anchor));

        var occurrenceOnlyIndex = currentContent.IndexOf(oldText, StringComparison.Ordinal);
        if (TryRemoveStandaloneLine(currentContent, oldText, occurrenceOnlyIndex, out var singleLineRemovedContent))
        {
            return singleLineRemovedContent;
        }

        return currentContent[..occurrenceOnlyIndex] + string.Empty + currentContent[(occurrenceOnlyIndex + oldText.Length)..];
    }

    private static bool TryRemoveStandaloneLine(
        string currentContent,
        string oldText,
        int occurrenceIndex,
        out string updatedContent)
    {
        updatedContent = string.Empty;

        if (occurrenceIndex < 0)
        {
            return false;
        }

        var lineStartIndex = occurrenceIndex == 0
            ? 0
            : currentContent.LastIndexOf('\n', occurrenceIndex - 1);
        lineStartIndex = lineStartIndex < 0 ? 0 : lineStartIndex + 1;

        var lineEndIndex = currentContent.IndexOfAny(['\r', '\n'], occurrenceIndex + oldText.Length);
        lineEndIndex = lineEndIndex < 0 ? currentContent.Length : lineEndIndex;

        var lineContent = currentContent[lineStartIndex..lineEndIndex];
        if (!string.Equals(lineContent.TrimEnd('\r'), oldText, StringComparison.Ordinal))
        {
            return false;
        }

        if (lineEndIndex < currentContent.Length)
        {
            if (currentContent[lineEndIndex] == '\r' && lineEndIndex + 1 < currentContent.Length && currentContent[lineEndIndex + 1] == '\n')
            {
                lineEndIndex += 2;
            }
            else
            {
                lineEndIndex += 1;
            }
        }

        updatedContent = currentContent[..lineStartIndex] + currentContent[lineEndIndex..];
        return true;
    }

    private static bool TryResolveRemoveOccurrenceIndex(
        string currentContent,
        string oldText,
        string anchor,
        out int occurrenceIndex)
    {
        occurrenceIndex = -1;

        var anchorMatch = string.IsNullOrWhiteSpace(anchor)
            ? null
            : PatchAnchorMatcher.TryResolve(currentContent, anchor, out var match)
                ? match
                : null;

        if (anchorMatch is null)
        {
            return false;
        }

        var indexes = new List<int>();
        var searchIndex = 0;
        while ((searchIndex = currentContent.IndexOf(oldText, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            indexes.Add(searchIndex);
            searchIndex += oldText.Length;
        }

        if (indexes.Count <= 1)
        {
            occurrenceIndex = indexes.FirstOrDefault();
            return occurrenceIndex >= 0;
        }

        var ranked = indexes
            .Select(index => new
            {
                Index = index,
                Distance = Math.Abs(index - anchorMatch.Index)
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Index)
            .ToArray();

        if (ranked.Length == 0 || (ranked.Length > 1 && ranked[0].Distance == ranked[1].Distance))
        {
            return false;
        }

        occurrenceIndex = ranked[0].Index;
        return true;
    }

    private static bool IsBinaryOrMediaPath(string path)
    {
        return BinaryAndMediaExtensions.Contains(Path.GetExtension(path));
    }

    private async Task<string> BuildPatchTextAsync(
        IReadOnlyList<PatchFileChange> fileChanges,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aibox-patch-edit-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(tempRoot, "original");
        var updatedRoot = Path.Combine(tempRoot, "updated");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(updatedRoot);

        try
        {
            foreach (var change in fileChanges)
            {
                var relativePath = NormalizeRelativePath(change.RelativePath);
                var originalPath = Path.Combine(originalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var updatedPath = Path.Combine(updatedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                var originalDirectory = Path.GetDirectoryName(originalPath);
                if (!string.IsNullOrWhiteSpace(originalDirectory))
                {
                    Directory.CreateDirectory(originalDirectory);
                }

                var updatedDirectory = Path.GetDirectoryName(updatedPath);
                if (!string.IsNullOrWhiteSpace(updatedDirectory))
                {
                    Directory.CreateDirectory(updatedDirectory);
                }

                await File.WriteAllTextAsync(originalPath, change.OldContent ?? string.Empty, cancellationToken);
                await File.WriteAllTextAsync(updatedPath, change.NewContent ?? string.Empty, cancellationToken);
            }

            var diffBuilder = new System.Text.StringBuilder();
            foreach (var change in fileChanges)
            {
                var relativePath = NormalizeRelativePath(change.RelativePath);
                var originalPath = Path.Combine(originalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var updatedPath = Path.Combine(updatedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                var fileDiff = await RunGitDiffAsync(originalPath, updatedPath, cancellationToken);
                diffBuilder.AppendLine(NormalizeGitDiffPaths(fileDiff, relativePath));
            }

            return diffBuilder.ToString().Trim();
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static async Task<string> RunGitDiffAsync(string originalPath, string updatedPath, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"diff --no-index --no-ext-diff --no-color --unified=3 \"{EscapeArgument(originalPath)}\" \"{EscapeArgument(updatedPath)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("Could not start git diff process.");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode is not 0 and not 1)
        {
            throw new InvalidOperationException($"git diff failed: {error.Trim()}");
        }

        return output;
    }

    private string NormalizeGitDiffPaths(string diffText, string relativePath)
    {
        var normalized = PatchDiffPathNormalizer.Normalize(diffText, relativePath, out var headerNormalizations);
        foreach (var header in headerNormalizations)
        {
            logger.LogInformation(
                "Normalized patch diff header. Original diff header: {OriginalDiffHeader}; Parsed old path: {ParsedOldPath}; Parsed new path: {ParsedNewPath}; Normalized path: {NormalizedOldPath} -> {NormalizedNewPath}",
                header.OriginalHeader,
                header.ParsedOldPath,
                header.ParsedNewPath,
                header.NormalizedOldPath,
                header.NormalizedNewPath);
        }

        return normalized;
    }

    private static bool ValidatePath(
        string rootPath,
        string relativePath,
        IReadOnlyDictionary<string, string> selectedContextMap,
        bool allowMissingFileForCreate,
        List<string> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            validationErrors.Add("Patch edit operation file path is empty.");
            return false;
        }

        if (Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith("/", StringComparison.Ordinal) ||
            relativePath.Contains("..", StringComparison.Ordinal))
        {
            validationErrors.Add($"Patch edit operation file path is invalid: {relativePath}");
            return false;
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            validationErrors.Add($"Patch edit operation file path is outside the project root: {relativePath}");
            return false;
        }

        if (!selectedContextMap.ContainsKey(relativePath))
        {
            if (!allowMissingFileForCreate)
            {
                validationErrors.Add($"Patch edit operation file path was not included in the selected context: {relativePath}");
                return false;
            }

            if (!HasContextRepresentative(relativePath, selectedContextMap))
            {
                validationErrors.Add($"Create operation requires a parent folder represented in the selected context: {relativePath}");
                return false;
            }
        }

        if (!File.Exists(fullPath))
        {
            if (!allowMissingFileForCreate)
            {
                validationErrors.Add($"Patch edit target file does not exist: {relativePath}");
                return false;
            }
        }

        return true;
    }

    private static bool HasContextRepresentative(string relativePath, IReadOnlyDictionary<string, string> selectedContextMap)
    {
        var parentDirectory = GetParentDirectory(relativePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return false;
        }

        return selectedContextMap.Keys.Any(contextPath =>
            string.Equals(GetParentDirectory(contextPath), parentDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private static PatchEditOperationEnvelope ParseResponse(string rawJson)
    {
        var normalizedJson = ExtractJsonPayload(rawJson);
        try
        {
            var response = JsonSerializer.Deserialize<PatchEditOperationEnvelope>(normalizedJson, JsonOptions);
            if (response is null)
            {
                throw new JsonException("JSON payload was empty.");
            }

            response.Operations ??= [];
            response.Errors ??= [];
            return response;
        }
        catch (JsonException exception)
        {
            throw new PatchPreviewValidationException(
                $"Patch preview validation failed:{Environment.NewLine}- JSON parse error: {exception.Message}",
                [$"JSON parse error: {exception.Message}"],
                rawJson ?? string.Empty,
                string.Empty,
                normalizedJson,
                ["JSON parse error"]);
        }
    }

    private static string ExtractJsonPayload(string rawJson)
    {
        var trimmed = (rawJson ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return trimmed;
        }

        var fencedBlock = TryExtractMarkdownFence(trimmed);
        if (!string.IsNullOrWhiteSpace(fencedBlock))
        {
            return fencedBlock;
        }

        return trimmed;
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

    private static PatchPreviewValidationException BuildValidationException(
        string rawJson,
        string normalizedDiff,
        IReadOnlyList<string> validationErrors,
        IReadOnlyList<PatchEditOperation> operations,
        IReadOnlyList<PatchReplaceDiagnostic>? replaceDiagnostics = null,
        IReadOnlyList<PatchSuggestedTargetDiagnostic>? suggestedTargetDiagnostics = null)
    {
        var errors = validationErrors.ToList();
        if (operations.Count == 0)
        {
            errors.Add("Patch preview operations list is empty.");
        }

        var grammarErrors = ExtractOperationGrammarErrors(errors);
        var friendlyMessage = grammarErrors.Count > 0
            ? "The model returned an invalid patch operation."
            : $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}";

        return new PatchPreviewValidationException(
            friendlyMessage,
            errors,
            rawJson ?? string.Empty,
            normalizedDiff ?? string.Empty,
            string.Empty,
            grammarErrors,
            replaceDiagnostics: replaceDiagnostics,
            suggestedTargetDiagnostics: suggestedTargetDiagnostics);
    }

    private static IReadOnlyList<string> ExtractOperationGrammarErrors(IReadOnlyList<string> validationErrors)
    {
        return validationErrors
            .Where(IsOperationGrammarError)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsOperationGrammarError(string error)
    {
        return ContainsAny(
            error,
            "requires a non-empty oldText",
            "requires a non-empty anchor",
            "must include non-empty newText",
            "unsupported operation",
            "JSON parse error",
            "patch preview operations list is empty",
            "exact target text was not found in context",
            "make the remove task more specific");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddReplaceDiagnostics(
        List<PatchReplaceDiagnostic> replaceDiagnostics,
        string filePath,
        string currentContent,
        string? oldText)
    {
        if (string.IsNullOrWhiteSpace(oldText))
        {
            return;
        }

        var xmlDocumentationRequested = IsXmlDocumentationRequested(oldText);
        var closestMatches = FindClosestReplaceMatches(currentContent, oldText, 3, xmlDocumentationRequested);
        var suggestedReplacementTarget = xmlDocumentationRequested
            ? SelectXmlDocumentationSuggestedTargetFromContent(currentContent)
            : closestMatches.FirstOrDefault()?.Snippet ?? string.Empty;

        replaceDiagnostics.Add(new PatchReplaceDiagnostic(
            filePath,
            oldText,
            suggestedReplacementTarget,
            closestMatches));
    }

    private static void AddSuggestedTargetDiagnostics(
        List<PatchSuggestedTargetDiagnostic> suggestedTargetDiagnostics,
        string filePath,
        string currentContent,
        string failedOperation,
        string targetLabel,
        string? requestedText)
    {
        if (string.IsNullOrWhiteSpace(requestedText))
        {
            return;
        }

        var xmlDocumentationRequested = IsXmlDocumentationRequested(requestedText);
        var closestMatches = FindClosestSuggestedTargetMatches(currentContent, requestedText, 3, xmlDocumentationRequested);
        var suggestedTargetText = xmlDocumentationRequested
            ? SelectXmlDocumentationSuggestedTargetFromContent(currentContent)
            : closestMatches.FirstOrDefault()?.Snippet ?? string.Empty;
        var recommendation = xmlDocumentationRequested
            ? "For adding documentation, use insert_before with the class or member declaration as anchor."
            : string.Empty;

        suggestedTargetDiagnostics.Add(new PatchSuggestedTargetDiagnostic(
            filePath,
            failedOperation,
            targetLabel,
            requestedText,
            suggestedTargetText,
            closestMatches,
            recommendation));
    }

    private static IReadOnlyList<PatchSuggestedTargetMatch> FindClosestSuggestedTargetMatches(
        string currentContent,
        string requestedText,
        int topCount,
        bool xmlDocumentationRequested)
    {
        return BuildReplaceCandidates(currentContent, requestedText, xmlDocumentationRequested)
            .Select(candidate => new
            {
                Candidate = candidate,
                Similarity = ComputeSimilarityScore(requestedText, candidate.Snippet),
                Priority = GetXmlDocumentationPriority(candidate, xmlDocumentationRequested)
            })
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Similarity)
            .ThenBy(item => item.Candidate.LineNumber)
            .Select(item => new PatchSuggestedTargetMatch(
                item.Candidate.LineNumber,
                item.Candidate.Snippet,
                item.Similarity))
            .Take(topCount)
            .ToArray();
    }

    private static IReadOnlyList<PatchReplaceClosestMatch> FindClosestReplaceMatches(
        string currentContent,
        string oldText,
        int topCount,
        bool xmlDocumentationRequested)
    {
        return BuildReplaceCandidates(currentContent, oldText, xmlDocumentationRequested)
            .Select(candidate => new
            {
                Candidate = candidate,
                Similarity = ComputeSimilarityScore(oldText, candidate.Snippet),
                Priority = GetXmlDocumentationPriority(candidate, xmlDocumentationRequested)
            })
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.Similarity)
            .ThenBy(item => item.Candidate.LineNumber)
            .Select(item => new PatchReplaceClosestMatch(
                item.Candidate.LineNumber,
                item.Candidate.Snippet,
                item.Similarity))
            .Take(topCount)
            .ToArray();
    }

    private static IEnumerable<ReplaceCandidate> BuildReplaceCandidates(string currentContent, string oldText, bool xmlDocumentationRequested)
    {
        var lines = currentContent.ReplaceLineEndings("\n").Split('\n');
        var requestedLineCount = Math.Max(1, oldText.ReplaceLineEndings("\n").Split('\n').Length);
        var windowSize = Math.Min(5, requestedLineCount);

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return new ReplaceCandidate(index + 1, line, IsXmlDocLine(line), IsDeclarationLine(line));
            }

            if (xmlDocumentationRequested)
            {
                if (IsXmlDocLine(line))
                {
                    var xmlBlockEnd = FindXmlDocBlockEnd(lines, index);
                    var xmlBlockSnippet = string.Join(Environment.NewLine, lines[index..xmlBlockEnd]).Trim();
                    if (!string.IsNullOrWhiteSpace(xmlBlockSnippet))
                    {
                        yield return new ReplaceCandidate(index + 1, xmlBlockSnippet, true, false);
                    }

                    var declarationIndex = FindFollowingDeclarationIndex(lines, xmlBlockEnd);
                    if (declarationIndex >= 0)
                    {
                        var declarationLine = lines[declarationIndex];
                        yield return new ReplaceCandidate(
                            declarationIndex + 1,
                            declarationLine.Trim(),
                            false,
                            IsDeclarationLine(declarationLine));
                    }
                }
                else if (IsDeclarationLine(line))
                {
                    var docBlockStart = FindPrecedingXmlDocBlockStart(lines, index);
                    if (docBlockStart >= 0)
                    {
                        var xmlBlockSnippet = string.Join(Environment.NewLine, lines[docBlockStart..index]).Trim();
                        if (!string.IsNullOrWhiteSpace(xmlBlockSnippet))
                        {
                            yield return new ReplaceCandidate(docBlockStart + 1, xmlBlockSnippet, true, false);
                        }
                    }
                }
            }

            if (windowSize <= 1)
            {
                continue;
            }

            var end = Math.Min(lines.Length, index + windowSize);
            if (end - index < windowSize)
            {
                continue;
            }

            var snippet = string.Join(Environment.NewLine, lines[index..end]).Trim();
            if (!string.IsNullOrWhiteSpace(snippet))
            {
                yield return new ReplaceCandidate(
                    index + 1,
                    snippet,
                    ContainsXmlDocBlock(snippet),
                    IsDeclarationBlock(snippet));
            }

        }
    }

    private static bool IsXmlDocumentationRequested(string? requestedText)
    {
        if (string.IsNullOrWhiteSpace(requestedText))
        {
            return false;
        }

        return ContainsAny(
            requestedText,
            "<summary>",
            "</summary>",
            "/// <summary>",
            "/// </summary>");
    }

    private static bool IsXmlDocLine(string line)
    {
        return line.TrimStart().StartsWith("///", StringComparison.Ordinal);
    }

    private static bool ContainsXmlDocBlock(string snippet)
    {
        return snippet.Contains("<summary>", StringComparison.OrdinalIgnoreCase) &&
               snippet.Contains("</summary>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeclarationLine(string line)
    {
        return GetDeclarationKind(line) is not DeclarationKind.None;
    }

    private static bool IsDeclarationBlock(string snippet)
    {
        return snippet.Split('\n').Any(IsDeclarationLine);
    }

    private static int FindXmlDocBlockEnd(string[] lines, int startIndex)
    {
        var index = startIndex;
        while (index < lines.Length && IsXmlDocLine(lines[index]))
        {
            index++;
        }

        return index;
    }

    private static int FindFollowingDeclarationIndex(string[] lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return IsDeclarationLine(line) ? index : -1;
        }

        return -1;
    }

    private static int FindPrecedingXmlDocBlockStart(string[] lines, int declarationIndex)
    {
        var index = declarationIndex - 1;
        while (index >= 0 && string.IsNullOrWhiteSpace(lines[index]))
        {
            index--;
        }

        if (index < 0 || !IsXmlDocLine(lines[index]))
        {
            return -1;
        }

        while (index - 1 >= 0 && IsXmlDocLine(lines[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int GetXmlDocumentationPriority(ReplaceCandidate candidate, bool xmlDocumentationRequested)
    {
        if (!xmlDocumentationRequested)
        {
            return 0;
        }

        var priority = 0;
        if (candidate.IsXmlDocBlock)
        {
            priority = 4;
        }
        else if (candidate.IsDeclarationLine)
        {
            priority = GetDeclarationKind(candidate.Snippet) switch
            {
                DeclarationKind.SealedClass => 3,
                DeclarationKind.Class => 3,
                DeclarationKind.Method => 2,
                DeclarationKind.Property => 1,
                DeclarationKind.Other => 0,
                _ => 0
            };
        }
        else if (candidate.Snippet.TrimStart().StartsWith("///", StringComparison.Ordinal))
        {
            priority = 2;
        }

        return priority;
    }

    private static string SelectXmlDocumentationSuggestedTargetFromContent(string currentContent)
    {
        var lines = currentContent.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            if (!IsXmlDocLine(lines[index]))
            {
                continue;
            }

            var xmlBlockEnd = FindXmlDocBlockEnd(lines, index);
            var xmlBlockSnippet = string.Join(Environment.NewLine, lines[index..xmlBlockEnd]).Trim();
            if (ContainsXmlDocBlock(xmlBlockSnippet))
            {
                return xmlBlockSnippet;
            }

            var declarationIndex = FindFollowingDeclarationIndex(lines, xmlBlockEnd);
            if (declarationIndex >= 0)
            {
                return lines[declarationIndex].Trim();
            }
        }

        var preferredDeclaration = SelectPreferredDeclarationLine(lines);
        if (!string.IsNullOrWhiteSpace(preferredDeclaration))
        {
            return preferredDeclaration;
        }

        return string.Empty;
    }

    private static string SelectPreferredDeclarationLine(string[] lines)
    {
        var bestDeclaration = string.Empty;
        var bestPriority = int.MinValue;

        foreach (var line in lines)
        {
            var kind = GetDeclarationKind(line);
            var priority = kind switch
            {
                DeclarationKind.SealedClass => 4,
                DeclarationKind.Class => 4,
                DeclarationKind.Method => 3,
                DeclarationKind.Property => 2,
                DeclarationKind.Other => 1,
                _ => 0
            };

            if (priority <= bestPriority)
            {
                continue;
            }

            if (kind is DeclarationKind.None)
            {
                continue;
            }

            bestPriority = priority;
            bestDeclaration = line.Trim();
        }

        return bestDeclaration;
    }

    private static DeclarationKind GetDeclarationKind(string snippet)
    {
        var trimmed = snippet.TrimStart();

        if (trimmed.StartsWith("public sealed class", StringComparison.Ordinal))
        {
            return DeclarationKind.SealedClass;
        }

        if (trimmed.StartsWith("public class", StringComparison.Ordinal) ||
            trimmed.StartsWith("public record", StringComparison.Ordinal) ||
            trimmed.StartsWith("public interface", StringComparison.Ordinal) ||
            trimmed.StartsWith("public struct", StringComparison.Ordinal) ||
            trimmed.StartsWith("public enum", StringComparison.Ordinal))
        {
            return DeclarationKind.Class;
        }

        if (!trimmed.StartsWith("public ", StringComparison.Ordinal))
        {
            return DeclarationKind.None;
        }

        if (trimmed.Contains("get;", StringComparison.Ordinal) ||
            trimmed.Contains("set;", StringComparison.Ordinal) ||
            trimmed.Contains("{ get", StringComparison.Ordinal) ||
            trimmed.Contains("=>", StringComparison.Ordinal))
        {
            return DeclarationKind.Property;
        }

        if (trimmed.Contains("(", StringComparison.Ordinal) && trimmed.Contains(")", StringComparison.Ordinal))
        {
            return DeclarationKind.Method;
        }

        if (trimmed.Contains("{", StringComparison.Ordinal))
        {
            return DeclarationKind.Property;
        }

        return DeclarationKind.Other;
    }

    private sealed record ReplaceCandidate(int LineNumber, string Snippet, bool IsXmlDocBlock, bool IsDeclarationLine);

    private enum DeclarationKind
    {
        None = 0,
        Other = 1,
        Property = 2,
        Method = 3,
        Class = 4,
        SealedClass = 5
    }

    private static double ComputeSimilarityScore(string expected, string candidate)
    {
        var normalizedExpected = NormalizeSimilarityText(expected);
        var normalizedCandidate = NormalizeSimilarityText(candidate);

        if (normalizedExpected.Length == 0 && normalizedCandidate.Length == 0)
        {
            return 100.0;
        }

        if (normalizedExpected.Length == 0 || normalizedCandidate.Length == 0)
        {
            return 0.0;
        }

        var distance = ComputeLevenshteinDistance(normalizedExpected, normalizedCandidate);
        var maxLength = Math.Max(normalizedExpected.Length, normalizedCandidate.Length);
        var similarity = 1.0 - ((double)distance / maxLength);
        return Math.Clamp(similarity * 100.0, 0.0, 100.0);
    }

    private static string NormalizeSimilarityText(string text)
    {
        return string.Join(
            " ",
            (text ?? string.Empty)
                .ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (left == right)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string ValidateProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new InvalidOperationException("Project path is required.");
        }

        var fullPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"Project path does not exist: {fullPath}");
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim().Trim('"');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith("a/", StringComparison.Ordinal) || normalized.StartsWith("b/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static string GetParentDirectory(string path)
    {
        var normalized = NormalizeRelativePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..index];
    }

    private static string EscapeArgument(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    private sealed class PatchEditOperationEnvelope
    {
        public IReadOnlyList<PatchEditOperation> Operations { get; set; } = [];
        public IReadOnlyList<string> Errors { get; set; } = [];
    }
}
