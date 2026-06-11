using System.Text.RegularExpressions;

namespace AiBox.DevPortal.Services;

internal sealed record PatchDiffHeaderNormalization(
    string OriginalHeader,
    string ParsedOldPath,
    string ParsedNewPath,
    string NormalizedOldPath,
    string NormalizedNewPath);

internal static partial class PatchDiffPathNormalizer
{
    public static string Normalize(
        string diffText,
        string relativePath,
        out IReadOnlyList<PatchDiffHeaderNormalization> headerNormalizations)
    {
        var normalizedRelativePath = NormalizeRelativePath(relativePath);
        var normalizations = new List<PatchDiffHeaderNormalization>();
        var lines = diffText.ReplaceLineEndings("\n").Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var headerMatch = DiffHeaderRegex().Match(lines[index]);
            if (headerMatch.Success)
            {
                var normalizedOldPath = $"a/{normalizedRelativePath}";
                var normalizedNewPath = $"b/{normalizedRelativePath}";
                normalizations.Add(new PatchDiffHeaderNormalization(
                    lines[index],
                    Unquote(headerMatch.Groups["old"].Value),
                    Unquote(headerMatch.Groups["new"].Value),
                    normalizedOldPath,
                    normalizedNewPath));
                lines[index] = $"diff --git {normalizedOldPath} {normalizedNewPath}";
                continue;
            }

            if (lines[index].StartsWith("--- ", StringComparison.Ordinal) &&
                !lines[index].Equals("--- /dev/null", StringComparison.Ordinal))
            {
                lines[index] = $"--- a/{normalizedRelativePath}";
                continue;
            }

            if (lines[index].StartsWith("+++ ", StringComparison.Ordinal) &&
                !lines[index].Equals("+++ /dev/null", StringComparison.Ordinal))
            {
                lines[index] = $"+++ b/{normalizedRelativePath}";
            }
        }

        headerNormalizations = normalizations;
        return string.Join('\n', lines).Trim();
    }

    internal static string NormalizeRelativePath(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim().Trim('"');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith("a/", StringComparison.Ordinal) ||
            normalized.StartsWith("b/", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static string Unquote(string path)
    {
        return path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1]
            : path;
    }

    [GeneratedRegex(@"^diff --git\s+(?<old>(?:""[^""]+""|\S+))\s+(?<new>(?:""[^""]+""|\S+))\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DiffHeaderRegex();
}
