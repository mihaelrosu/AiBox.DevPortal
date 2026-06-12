using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class PatchContextCoverageAnalyzer
{
    public static PatchContextCoverage Analyze(
        string patchText,
        IReadOnlyList<LocalCoderFileContext> contextFiles)
    {
        var normalizedContexts = (contextFiles ?? [])
            .Select(context => PatchDiffPathNormalizer.NormalizeRelativePath(context.RelativePath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var files = ExtractModifiedFiles(patchText)
            .Select(path => AnalyzeFile(path, normalizedContexts))
            .ToArray();

        return new PatchContextCoverage
        {
            Files = files,
            RiskScore = files.Sum(file => file.RiskReasons.Count)
        };
    }

    private static PatchContextCoverageFile AnalyzeFile(
        string relativePath,
        IReadOnlyList<string> contextPaths)
    {
        var category = contextPaths.Contains(relativePath, StringComparer.OrdinalIgnoreCase)
            ? PatchContextCoverageCategory.ContextFile
            : IsRelatedFile(relativePath, contextPaths)
                ? PatchContextCoverageCategory.RelatedFile
                : PatchContextCoverageCategory.UnknownFile;

        var reasons = new List<string>();
        if (category != PatchContextCoverageCategory.ContextFile)
        {
            reasons.Add("Modified file was not provided as context.");
        }

        var fileName = Path.GetFileName(relativePath);
        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Application startup file.");
        }

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Application settings file.");
        }

        if (ContainsSensitiveTerm(relativePath, "auth") ||
            ContainsSensitiveTerm(relativePath, "authentication") ||
            ContainsSensitiveTerm(relativePath, "identity"))
        {
            reasons.Add("Authentication code.");
        }

        if (relativePath.Contains("DbContext", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Database context.");
        }

        return new PatchContextCoverageFile
        {
            RelativePath = relativePath,
            Category = category,
            RiskReasons = reasons
        };
    }

    private static IReadOnlyList<string> ExtractModifiedFiles(string patchText)
    {
        var paths = new List<string>();
        foreach (var line in (patchText ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            if (!line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            var path = PatchDiffPathNormalizer.NormalizeRelativePath(parts[3]);
            if (!string.IsNullOrWhiteSpace(path) && !path.Equals("dev/null", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsRelatedFile(string changedPath, IReadOnlyList<string> contextPaths)
    {
        var changedDirectory = Path.GetDirectoryName(changedPath)?.Replace('\\', '/') ?? string.Empty;
        var changedStem = Path.GetFileNameWithoutExtension(changedPath);

        foreach (var contextPath in contextPaths)
        {
            var contextDirectory = Path.GetDirectoryName(contextPath)?.Replace('\\', '/') ?? string.Empty;
            var contextStem = Path.GetFileNameWithoutExtension(contextPath);

            if (!string.IsNullOrWhiteSpace(changedDirectory) &&
                changedDirectory.Equals(contextDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (changedStem.StartsWith(contextStem, StringComparison.OrdinalIgnoreCase) ||
                contextStem.StartsWith(changedStem, StringComparison.OrdinalIgnoreCase) ||
                HasPathSegment(changedPath, contextStem))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasPathSegment(string path, string value)
    {
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsSensitiveTerm(string path, string value)
    {
        return path.Split(['/', '\\', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
}
