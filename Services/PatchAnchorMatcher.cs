using System.Net;
using System.Text.RegularExpressions;

namespace AiBox.DevPortal.Services;

internal enum PatchAnchorMatchStrategy
{
    ExactHtml,
    ExactText,
    FuzzyText
}

internal sealed record PatchAnchorMatch(
    int Index,
    int Length,
    string OriginalAnchor,
    string NormalizedAnchor,
    PatchAnchorMatchStrategy Strategy);

internal static partial class PatchAnchorMatcher
{
    public static bool TryResolve(string content, string anchor, out PatchAnchorMatch? match)
    {
        match = null;

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(anchor))
        {
            return false;
        }

        var originalAnchor = anchor;
        var normalizedAnchor = NormalizeVisibleText(anchor);
        var containsHtmlTags = HtmlTagRegex().IsMatch(anchor);
        var exactIndex = content.IndexOf(anchor, StringComparison.Ordinal);

        if (exactIndex >= 0)
        {
            match = new PatchAnchorMatch(
                exactIndex,
                anchor.Length,
                originalAnchor,
                normalizedAnchor,
                containsHtmlTags ? PatchAnchorMatchStrategy.ExactHtml : PatchAnchorMatchStrategy.ExactText);
            return true;
        }

        if (containsHtmlTags &&
            !string.IsNullOrWhiteSpace(normalizedAnchor) &&
            TryFindUnique(content, normalizedAnchor, StringComparison.Ordinal, out var textIndex))
        {
            var span = ExpandToContainingMarkup(content, textIndex, normalizedAnchor.Length);
            match = new PatchAnchorMatch(
                span.Index,
                span.Length,
                originalAnchor,
                normalizedAnchor,
                PatchAnchorMatchStrategy.ExactText);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedAnchor) &&
            TryFindUniqueFuzzyText(content, normalizedAnchor, out var fuzzyIndex, out var fuzzyLength))
        {
            var span = ExpandToContainingMarkup(content, fuzzyIndex, fuzzyLength);
            match = new PatchAnchorMatch(
                span.Index,
                span.Length,
                originalAnchor,
                normalizedAnchor,
                PatchAnchorMatchStrategy.FuzzyText);
            return true;
        }

        return false;
    }

    private static string NormalizeVisibleText(string anchor)
    {
        var withoutTags = HtmlTagRegex().Replace(anchor, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex().Replace(decoded, " ").Trim();
    }

    private static bool TryFindUnique(
        string content,
        string value,
        StringComparison comparison,
        out int matchIndex)
    {
        matchIndex = content.IndexOf(value, comparison);
        if (matchIndex < 0)
        {
            return false;
        }

        return content.IndexOf(value, matchIndex + value.Length, comparison) < 0;
    }

    private static bool TryFindUniqueFuzzyText(
        string content,
        string normalizedAnchor,
        out int matchIndex,
        out int matchLength)
    {
        matchIndex = -1;
        matchLength = 0;

        var words = normalizedAnchor
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape)
            .ToArray();

        if (words.Length == 0)
        {
            return false;
        }

        var regex = new Regex(
            string.Join(@"\s+", words),
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var matches = regex.Matches(content);

        if (matches.Count != 1)
        {
            return false;
        }

        matchIndex = matches[0].Index;
        matchLength = matches[0].Length;
        return true;
    }

    private static (int Index, int Length) ExpandToContainingMarkup(
        string content,
        int matchIndex,
        int matchLength)
    {
        var previousTagStart = content.LastIndexOf('<', matchIndex);
        var previousTagEnd = content.LastIndexOf('>', matchIndex);

        if (previousTagStart > previousTagEnd)
        {
            var tagEnd = content.IndexOf('>', matchIndex + matchLength);
            if (tagEnd >= 0)
            {
                return (previousTagStart, tagEnd - previousTagStart + 1);
            }
        }

        if (previousTagStart >= 0 && previousTagEnd >= previousTagStart)
        {
            var openingTag = content[previousTagStart..(previousTagEnd + 1)];
            var tagNameMatch = OpeningTagNameRegex().Match(openingTag);

            if (tagNameMatch.Success && !openingTag.EndsWith("/>", StringComparison.Ordinal))
            {
                var closingTag = $"</{tagNameMatch.Groups[1].Value}>";
                var closingTagStart = content.IndexOf(closingTag, matchIndex + matchLength, StringComparison.Ordinal);

                if (closingTagStart >= 0)
                {
                    return (previousTagStart, closingTagStart + closingTag.Length - previousTagStart);
                }
            }
        }

        return (matchIndex, matchLength);
    }

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^<\s*([A-Za-z][A-Za-z0-9_.:-]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpeningTagNameRegex();
}
