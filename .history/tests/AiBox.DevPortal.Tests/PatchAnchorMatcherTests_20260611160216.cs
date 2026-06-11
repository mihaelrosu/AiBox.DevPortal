using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchAnchorMatcherTests
{
    [Fact]
    public void TryResolve_ExactHtmlAnchor_UsesExactHtmlStrategy()
    {
        const string anchor = "<h3>Patch Queue</h3>";
        const string content = $"<section>{anchor}</section>";

        var resolved = PatchAnchorMatcher.TryResolve(content, anchor, out var match);

        Assert.True(resolved);
        Assert.NotNull(match);
        Assert.Equal(PatchAnchorMatchStrategy.ExactHtml, match.Strategy);
        Assert.Equal(anchor, content.Substring(match.Index, match.Length));
        Assert.Equal("Patch Queue", match.NormalizedAnchor);
    }

    [Fact]
    public void TryResolve_HtmlAnchorWithUniqueVisibleText_UsesExactTextFallback()
    {
        const string anchor = "<h3>Patch Queue</h3>";
        const string actualMarkup = "<h5>Patch Queue</h5>";
        const string content = $"<section>{actualMarkup}</section>";

        var resolved = PatchAnchorMatcher.TryResolve(content, anchor, out var match);

        Assert.True(resolved);
        Assert.NotNull(match);
        Assert.Equal(PatchAnchorMatchStrategy.ExactText, match.Strategy);
        Assert.Equal(actualMarkup, content.Substring(match.Index, match.Length));
        Assert.Equal("Patch Queue", match.NormalizedAnchor);
    }

    [Fact]
    public void TryResolve_RadzenTextContainingAnchorText_ResolvesWholeComponent()
    {
        const string anchor = "<h3>Patch Queue</h3>";
        const string radzenText = "<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H5\" />";
        var content = $"<RadzenStack>{Environment.NewLine}    {radzenText}{Environment.NewLine}</RadzenStack>";

        var resolved = PatchAnchorMatcher.TryResolve(content, anchor, out var match);

        Assert.True(resolved);
        Assert.NotNull(match);
        Assert.Equal(PatchAnchorMatchStrategy.ExactText, match.Strategy);
        Assert.Equal(radzenText, content.Substring(match.Index, match.Length));
    }

    [Fact]
    public void TryResolve_UniqueTextWithCaseAndWhitespaceDifferences_UsesFuzzyTextStrategy()
    {
        const string radzenText = "<RadzenText Text=\"PATCH   QUEUE\" TextStyle=\"TextStyle.H5\" />";

        var resolved = PatchAnchorMatcher.TryResolve(radzenText, "<h3>Patch Queue</h3>", out var match);

        Assert.True(resolved);
        Assert.NotNull(match);
        Assert.Equal(PatchAnchorMatchStrategy.FuzzyText, match.Strategy);
        Assert.Equal(radzenText, radzenText.Substring(match.Index, match.Length));
    }

    [Fact]
    public void TryResolve_MissingAnchor_ReturnsFalse()
    {
        const string content = "<RadzenText Text=\"Patch History\" TextStyle=\"TextStyle.H5\" />";

        var resolved = PatchAnchorMatcher.TryResolve(content, "<h3>Patch Queue</h3>", out var match);

        Assert.False(resolved);
        Assert.Null(match);
    }

    [Fact]
    public void TryResolve_DuplicateVisibleText_ReturnsFalse()
    {
        var content = string.Join(
            Environment.NewLine,
            "<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H5\" />",
            "<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H6\" />");

        var resolved = PatchAnchorMatcher.TryResolve(content, "<h3>Patch Queue</h3>", out var match);

        Assert.False(resolved);
        Assert.Null(match);
    }
}
