using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PlannerContextSelectionService
{
    public PlannerContextSelectionResult SelectContextFiles(PlannerResult plannerResult, ProjectKnowledgeIndex index)
    {
        ArgumentNullException.ThrowIfNull(plannerResult);
        ArgumentNullException.ThrowIfNull(index);

        var indexByPath = BuildIndexLookup(index);
        var editableFiles = new List<string>();
        var instructionFiles = new List<string>();
        var missingFiles = new List<string>();
        var warnings = new List<string>();

        foreach (var targetFile in NormalizePaths(plannerResult.TargetFiles))
        {
            if (IsAgentsFile(targetFile))
            {
                SelectInstructionFile(targetFile, indexByPath, instructionFiles, warnings, isTargetFile: true, missingFiles);
                continue;
            }

            if (TryResolveIndexedPath(indexByPath, targetFile, out var indexedPath))
            {
                AddUnique(editableFiles, indexedPath);
                continue;
            }

            AddUnique(missingFiles, targetFile);
        }

        foreach (var instructionFile in NormalizePaths(plannerResult.InstructionFiles))
        {
            if (IsAgentsFile(instructionFile))
            {
                SelectInstructionFile(instructionFile, indexByPath, instructionFiles, warnings, isTargetFile: false, missingFiles);
                continue;
            }

            if (!TryResolveIndexedPath(indexByPath, instructionFile, out var indexedPath))
            {
                warnings.Add($"Instruction file '{instructionFile}' was not found in the project knowledge index.");
                continue;
            }

            if (editableFiles.Contains(indexedPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            AddUnique(instructionFiles, indexedPath);
        }

        if (editableFiles.Count == 0)
        {
            warnings.Add("PatchBuilder is blocked because PlannerResult.targetFiles did not resolve to any editable files.");
        }

        return new PlannerContextSelectionResult
        {
            EditableFiles = editableFiles,
            InstructionFiles = instructionFiles,
            MissingFiles = missingFiles,
            Warnings = warnings,
            Rules = NormalizePaths(plannerResult.Rules)
        };
    }

    private static void SelectInstructionFile(
        string relativePath,
        IReadOnlyDictionary<string, string> indexByPath,
        List<string> instructionFiles,
        List<string> warnings,
        bool isTargetFile,
        List<string> missingFiles)
    {
        if (TryResolveIndexedPath(indexByPath, relativePath, out var indexedPath))
        {
            AddUnique(instructionFiles, indexedPath);

            if (isTargetFile)
            {
                warnings.Add($"AGENTS.md file '{indexedPath}' was treated as read-only instruction context.");
            }

            return;
        }

        if (isTargetFile)
        {
            AddUnique(missingFiles, relativePath);
        }
        else
        {
            warnings.Add($"Instruction file '{relativePath}' was not found in the project knowledge index.");
        }
    }

    private static IReadOnlyDictionary<string, string> BuildIndexLookup(ProjectKnowledgeIndex index)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in index.Items ?? [])
        {
            var normalizedPath = NormalizePath(item.RelativePath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || lookup.ContainsKey(normalizedPath))
            {
                continue;
            }

            lookup[normalizedPath] = normalizedPath;
        }

        return lookup;
    }

    private static bool TryResolveIndexedPath(IReadOnlyDictionary<string, string> indexByPath, string relativePath, out string indexedPath)
    {
        indexedPath = string.Empty;
        var normalizedPath = NormalizePath(relativePath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        if (!indexByPath.TryGetValue(normalizedPath, out var resolvedPath) || string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        indexedPath = resolvedPath;
        return true;
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths)
    {
        return (paths ?? [])
            .Select(NormalizePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePath(string relativePath)
    {
        var normalized = relativePath?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Trim();
    }

    private static bool IsAgentsFile(string relativePath)
    {
        return Path.GetFileName(relativePath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }
}
