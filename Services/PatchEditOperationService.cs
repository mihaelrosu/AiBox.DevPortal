using System.Diagnostics;
using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PatchEditOperationService(ILogger<PatchEditOperationService> logger) : IPatchEditOperationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert_after",
        "insert_before",
        "replace"
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
        var selectedContextMap = selectedFileContexts
            .ToDictionary(context => NormalizeRelativePath(context.RelativePath), context => context.Content, StringComparer.OrdinalIgnoreCase);

        if (selectedContextMap.Count == 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                ["Patch preview requires selected file context."],
                response.Operations);
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

        if (response.Operations.Count == 0)
        {
            throw BuildValidationException(
                rawJson,
                string.Empty,
                ["Patch preview operations list is empty."],
                response.Operations);
        }

        foreach (var operation in response.Operations)
        {
            var filePath = NormalizeRelativePath(operation.FilePath);
            if (!ValidatePath(rootPath, filePath, selectedContextMap, validationErrors))
            {
                continue;
            }

            var currentPath = Path.Combine(rootPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(currentPath))
            {
                validationErrors.Add($"Patch edit target file does not exist: {filePath}");
                continue;
            }

            if (!fileStateMap.TryGetValue(filePath, out var currentContent))
            {
                currentContent = originalContentMap[filePath];
            }

            var result = ApplyOperation(operation, filePath, currentContent, validationErrors);
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
                response.Operations);
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
                response.Operations);
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
        List<string> validationErrors)
    {
        var normalizedOperation = operation.Operation.Trim();
        if (!AllowedOperations.Contains(normalizedOperation))
        {
            validationErrors.Add($"Unsupported operation '{operation.Operation}' for file '{filePath}'.");
            return null;
        }

        var anchor = operation.Anchor ?? string.Empty;
        var oldText = operation.OldText ?? string.Empty;
        var newText = operation.NewText ?? string.Empty;

        if (string.IsNullOrWhiteSpace(newText))
        {
            validationErrors.Add($"Operation '{normalizedOperation}' for file '{filePath}' must include non-empty newText.");
            return null;
        }

        return normalizedOperation.ToLowerInvariant() switch
        {
            "insert_before" => InsertBefore(filePath, currentContent, anchor, newText, validationErrors),
            "insert_after" => InsertAfter(filePath, currentContent, anchor, newText, validationErrors),
            "replace" => ReplaceText(filePath, currentContent, oldText, newText, validationErrors),
            _ => null
        };
    }

    private string? InsertBefore(
        string filePath,
        string currentContent,
        string anchor,
        string newText,
        List<string> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            validationErrors.Add($"Operation 'insert_before' for file '{filePath}' requires a non-empty anchor.");
            return null;
        }

        if (!TryResolveAnchor("insert_before", filePath, currentContent, anchor, out var match))
        {
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
        List<string> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(anchor))
        {
            validationErrors.Add($"Operation 'insert_after' for file '{filePath}' requires a non-empty anchor.");
            return null;
        }

        if (!TryResolveAnchor("insert_after", filePath, currentContent, anchor, out var match))
        {
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
        List<string> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(oldText))
        {
            validationErrors.Add($"Operation 'replace' for file '{filePath}' requires a non-empty oldText.");
            return null;
        }

        var index = currentContent.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
        {
            validationErrors.Add($"Old text not found for replace in file '{filePath}': {oldText}");
            return null;
        }

        return currentContent[..index] + newText + currentContent[(index + oldText.Length)..];
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
            validationErrors.Add($"Patch edit operation file path was not included in the selected context: {relativePath}");
            return false;
        }

        if (!File.Exists(fullPath))
        {
            validationErrors.Add($"Patch edit target file does not exist: {relativePath}");
            return false;
        }

        return true;
    }

    private static PatchEditOperationEnvelope ParseResponse(string rawJson)
    {
        try
        {
            var normalizedJson = UnwrapMarkdownCodeFence(rawJson);
            var response = JsonSerializer.Deserialize<PatchEditOperationEnvelope>(normalizedJson, JsonOptions);
            if (response is null)
            {
                throw new JsonException("JSON payload was empty.");
            }

            response.Operations ??= [];
            return response;
        }
        catch (JsonException exception)
        {
            throw new PatchPreviewValidationException(
                $"Patch preview validation failed:{Environment.NewLine}- JSON parse error: {exception.Message}",
                [$"JSON parse error: {exception.Message}"],
                rawJson ?? string.Empty,
                string.Empty);
        }
    }

    private static string UnwrapMarkdownCodeFence(string rawJson)
    {
        var trimmed = (rawJson ?? string.Empty).Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var openingLineEnd = trimmed.IndexOf('\n');
        if (openingLineEnd < 0)
        {
            return trimmed;
        }

        var openingFence = trimmed[..openingLineEnd].TrimEnd('\r');
        if (!openingFence.Equals("```", StringComparison.Ordinal) &&
            !openingFence.Equals("```json", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var closingLineStart = trimmed.LastIndexOf('\n');
        if (closingLineStart <= openingLineEnd)
        {
            return trimmed;
        }

        var closingFence = trimmed[(closingLineStart + 1)..].Trim();
        if (!closingFence.Equals("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed[(openingLineEnd + 1)..closingLineStart].Trim();
    }

    private static PatchPreviewValidationException BuildValidationException(
        string rawJson,
        string normalizedDiff,
        IReadOnlyList<string> validationErrors,
        IReadOnlyList<PatchEditOperation> operations)
    {
        var errors = validationErrors.ToList();
        if (operations.Count == 0)
        {
            errors.Add("Patch preview operations list is empty.");
        }

        return new PatchPreviewValidationException(
            $"Patch preview validation failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", errors)}",
            errors,
            rawJson ?? string.Empty,
            normalizedDiff ?? string.Empty);
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

    private static string EscapeArgument(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    private sealed class PatchEditOperationEnvelope
    {
        public IReadOnlyList<PatchEditOperation> Operations { get; set; } = [];
    }
}
