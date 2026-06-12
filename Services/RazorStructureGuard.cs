using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class RazorStructureGuard
{
    private const int RegionLookaround = 2;

    public static RazorStructureGuardResult Analyze(string patchText, string task)
    {
        var lines = (patchText ?? string.Empty)
            .ReplaceLineEndings("\n")
            .Split('\n');

        var hunks = new List<RazorStructureHunkClassification>();
        var errors = new List<string>();

        string currentFilePath = string.Empty;
        string currentHunkHeader = string.Empty;
        var currentHunkLines = new List<string>();

        void FlushHunk()
        {
            if (string.IsNullOrWhiteSpace(currentFilePath) ||
                !LooksLikeRazorFile(currentFilePath) ||
                string.IsNullOrWhiteSpace(currentHunkHeader) ||
                currentHunkLines.Count == 0)
            {
                currentHunkLines.Clear();
                return;
            }

            var classification = ClassifyHunk(currentFilePath, currentHunkHeader, currentHunkLines);
            hunks.Add(classification);

            if (classification.Region is not RazorStructureRegion.MarkupRegion &&
                classification.Region is not RazorStructureRegion.UnknownRegion &&
                !TaskExplicitlyRequestsRegion(task, classification.Region))
            {
                errors.Add($"Patch modifies protected Razor region: {classification.Region}");
            }

            currentHunkLines.Clear();
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentFilePath = StripDiffPathPrefix(line[4..]);
                continue;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushHunk();
                currentHunkHeader = line;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(currentHunkHeader))
            {
                currentHunkLines.Add(line);
            }
        }

        FlushHunk();

        return new RazorStructureGuardResult
        {
            Hunks = hunks,
            Errors = errors.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static RazorStructureHunkClassification ClassifyHunk(
        string filePath,
        string hunkHeader,
        IReadOnlyList<string> hunkLines)
    {
        var changedLines = hunkLines
            .Select((line, index) => new { Line = line, Index = index })
            .Where(entry => entry.Line.Length > 0 &&
                ((entry.Line.StartsWith("+", StringComparison.Ordinal) && !entry.Line.StartsWith("+++", StringComparison.Ordinal)) ||
                 (entry.Line.StartsWith("-", StringComparison.Ordinal) && !entry.Line.StartsWith("---", StringComparison.Ordinal))))
            .Select(entry => new RazorStructureLineClassification
            {
                Text = entry.Line[1..].Trim(),
                Region = ClassifyChangedLine(hunkLines, entry.Index)
            })
            .ToArray();

        var region = PickRegion(changedLines.Select(line => line.Region).ToArray());

        return new RazorStructureHunkClassification
        {
            FilePath = filePath,
            HunkHeader = hunkHeader,
            Region = region,
            ChangedLines = changedLines
        };
    }

    private static RazorStructureRegion ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return RazorStructureRegion.UnknownRegion;
        }

        var trimmed = line.TrimStart();
        if ((trimmed.StartsWith("+", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal)) &&
            trimmed.Length > 1 &&
            !trimmed.StartsWith("+++", StringComparison.Ordinal) &&
            !trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        if (IsDirectiveLine(trimmed))
        {
            return RazorStructureRegion.DirectiveRegion;
        }

        if (IsImportLine(trimmed))
        {
            return RazorStructureRegion.ImportRegion;
        }

        if (IsDocumentationLine(trimmed))
        {
            return RazorStructureRegion.DocumentationRegion;
        }

        if (IsCodeLine(trimmed))
        {
            return RazorStructureRegion.CodeRegion;
        }

        if (trimmed.StartsWith("<", StringComparison.Ordinal) ||
            trimmed.IndexOf('<') >= 0 ||
            trimmed.IndexOf('>') >= 0)
        {
            return RazorStructureRegion.MarkupRegion;
        }

        return RazorStructureRegion.MarkupRegion;
    }

    private static RazorStructureRegion ClassifyChangedLine(IReadOnlyList<string> hunkLines, int changedLineIndex)
    {
        var currentLine = hunkLines[changedLineIndex];
        var currentRegion = ClassifyPatchLine(currentLine);
        if (currentRegion is not RazorStructureRegion.MarkupRegion and not RazorStructureRegion.UnknownRegion)
        {
            return currentRegion;
        }

        for (var offset = 1; offset <= RegionLookaround; offset++)
        {
            var index = changedLineIndex - offset;
            if (index < 0)
            {
                break;
            }

            var line = hunkLines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var region = ClassifyPatchLine(line);
            if (region is not RazorStructureRegion.MarkupRegion and not RazorStructureRegion.UnknownRegion)
            {
                return region;
            }
        }

        for (var offset = 1; offset <= RegionLookaround; offset++)
        {
            var index = changedLineIndex + offset;
            if (index >= hunkLines.Count)
            {
                break;
            }

            var line = hunkLines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            var region = ClassifyPatchLine(line);
            if (region is not RazorStructureRegion.MarkupRegion and not RazorStructureRegion.UnknownRegion)
            {
                return region;
            }
        }

        return RazorStructureRegion.MarkupRegion;
    }

    private static RazorStructureRegion ClassifyPatchLine(string line)
    {
        var trimmed = line.TrimStart();
        if ((trimmed.StartsWith("+", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal)) &&
            trimmed.Length > 1 &&
            !trimmed.StartsWith("+++", StringComparison.Ordinal) &&
            !trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return ClassifyLine(trimmed);
    }

    private static bool IsDirectiveLine(string line)
    {
        return line.StartsWith("@page", StringComparison.Ordinal) ||
               line.StartsWith("@rendermode", StringComparison.Ordinal) ||
               line.StartsWith("@layout", StringComparison.Ordinal) ||
               line.StartsWith("@attribute", StringComparison.Ordinal);
    }

    private static bool IsImportLine(string line)
    {
        return line.StartsWith("@using", StringComparison.Ordinal) ||
               line.StartsWith("@inject", StringComparison.Ordinal);
    }

    private static bool IsDocumentationLine(string line)
    {
        return line.StartsWith("///", StringComparison.Ordinal) ||
               line.Contains("<summary>", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("</summary>", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodeLine(string line)
    {
        return line.StartsWith("@code", StringComparison.Ordinal) ||
               line.StartsWith("private ", StringComparison.Ordinal) ||
               line.StartsWith("public ", StringComparison.Ordinal) ||
               line.StartsWith("protected ", StringComparison.Ordinal) ||
               line.StartsWith("internal ", StringComparison.Ordinal) ||
               line.StartsWith("static ", StringComparison.Ordinal) ||
               line.StartsWith("readonly ", StringComparison.Ordinal) ||
               line.StartsWith("const ", StringComparison.Ordinal) ||
               line.StartsWith("async ", StringComparison.Ordinal) ||
               line.StartsWith("partial ", StringComparison.Ordinal) ||
               line.StartsWith("sealed ", StringComparison.Ordinal) ||
               line.StartsWith("record ", StringComparison.Ordinal) ||
               line.StartsWith("class ", StringComparison.Ordinal) ||
               line.StartsWith("interface ", StringComparison.Ordinal) ||
               line.StartsWith("enum ", StringComparison.Ordinal) ||
               line.Contains("=>", StringComparison.Ordinal) ||
               line.EndsWith(";", StringComparison.Ordinal) ||
               line.Contains(" Task<", StringComparison.Ordinal) ||
               line.Contains(" void ", StringComparison.Ordinal);
    }

    private static bool LooksLikeRazorFile(string filePath)
    {
        return filePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDiffPathPrefix(string path)
    {
        var trimmed = (path ?? string.Empty).Trim();
        if (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        return trimmed;
    }

    private static bool TaskExplicitlyRequestsRegion(string task, RazorStructureRegion region)
    {
        var normalized = (task ?? string.Empty).ToLowerInvariant();
        return region switch
        {
            RazorStructureRegion.DirectiveRegion => normalized.Contains("@page") ||
                                                    normalized.Contains("@layout") ||
                                                    normalized.Contains("@rendermode") ||
                                                    normalized.Contains("@attribute") ||
                                                    normalized.Contains("directive") ||
                                                    normalized.Contains("page route"),
            RazorStructureRegion.ImportRegion => normalized.Contains("@using") ||
                                                 normalized.Contains("@inject") ||
                                                 normalized.Contains("import") ||
                                                 normalized.Contains("inject"),
            RazorStructureRegion.DocumentationRegion => normalized.Contains("summary") ||
                                                         normalized.Contains("documentation") ||
                                                         normalized.Contains("xml doc") ||
                                                         normalized.Contains("header comment") ||
                                                         normalized.Contains("///"),
            RazorStructureRegion.CodeRegion => normalized.Contains("@code") ||
                                               normalized.Contains("code-behind") ||
                                               normalized.Contains("code block") ||
                                               normalized.Contains("code section") ||
                                               normalized.Contains("method") ||
                                               normalized.Contains("logic") ||
                                               normalized.Contains("service") ||
                                               normalized.Contains("dependency injection") ||
                                               normalized.Contains("injected service"),
            _ => true
        };
    }

    private static RazorStructureRegion PickRegion(IReadOnlyList<RazorStructureRegion> regions)
    {
        if (regions.Count == 0)
        {
            return RazorStructureRegion.UnknownRegion;
        }

        if (regions.Contains(RazorStructureRegion.DirectiveRegion))
        {
            return RazorStructureRegion.DirectiveRegion;
        }

        if (regions.Contains(RazorStructureRegion.ImportRegion))
        {
            return RazorStructureRegion.ImportRegion;
        }

        if (regions.Contains(RazorStructureRegion.DocumentationRegion))
        {
            return RazorStructureRegion.DocumentationRegion;
        }

        if (regions.Contains(RazorStructureRegion.CodeRegion))
        {
            return RazorStructureRegion.CodeRegion;
        }

        if (regions.Contains(RazorStructureRegion.MarkupRegion))
        {
            return RazorStructureRegion.MarkupRegion;
        }

        return RazorStructureRegion.UnknownRegion;
    }
}
