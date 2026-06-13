using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class RazorStructureGuardTests
{
    [Fact]
    public void Analyze_PageMarkupChange_AllowsMarkupRegion()
    {
        var result = RazorStructureGuard.Analyze("""
                                                diff --git a/Example.razor b/Example.razor
                                                --- a/Example.razor
                                                +++ b/Example.razor
                                                @@ -1,4 +1,5 @@
                                                 @page "/"
                                                 <PageTitle>Example</PageTitle>
                                                 <h1>Example</h1>
                                                +<RadzenAlert Text="Hello" />
                                                """, "Update the page header markup.");

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(RazorStructureRegion.MarkupRegion, hunk.Region);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Analyze_SummaryBlockChange_BlocksDocumentationRegion()
    {
        var result = RazorStructureGuard.Analyze("""
                                                diff --git a/Example.razor b/Example.razor
                                                --- a/Example.razor
                                                +++ b/Example.razor
                                                @@ -8,5 +8,6 @@
                                                 <div>
                                                     <summary>
                                                +        <RadzenAlert Text="Hello" />
                                                     Visible docs
                                                     </summary>
                                                 </div>
                                                """, "Update the page header markup.");

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(RazorStructureRegion.DocumentationRegion, hunk.Region);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("DocumentationRegion", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_PageDirectiveChange_BlocksDirectiveRegion()
    {
        var diff = string.Join(Environment.NewLine,
            "diff --git a/Example.razor b/Example.razor",
            "--- a/Example.razor",
            "+++ b/Example.razor",
            "@@ -1,3 +1,3 @@",
            "-@page \"/old\"",
            "+@page \"/new\"",
            " <h1>Example</h1>");

        var result = RazorStructureGuard.Analyze(diff, "Update the page header markup.");

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(RazorStructureRegion.DirectiveRegion, hunk.Region);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("DirectiveRegion", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_UsingDirectiveChange_BlocksImportRegion()
    {
        var diff = string.Join(Environment.NewLine,
            "diff --git a/Example.razor b/Example.razor",
            "--- a/Example.razor",
            "+++ b/Example.razor",
            "@@ -1,3 +1,3 @@",
            "-@using Foo.Bar",
            "+@using Foo.Baz",
            " <h1>Example</h1>");

        var result = RazorStructureGuard.Analyze(diff, "Update the page header markup.");

        var hunk = Assert.Single(result.Hunks);
        Assert.Equal(RazorStructureRegion.ImportRegion, hunk.Region);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("ImportRegion", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CodeBlockChange_BlocksUnlessExplicitlyRequested()
    {
        var blocked = RazorStructureGuard.Analyze("""
                                                 diff --git a/Example.razor b/Example.razor
                                                 --- a/Example.razor
                                                 +++ b/Example.razor
                                                 @@ -10,6 +10,7 @@
                                                  @code {
                                                 -    private string Message { get; set; } = string.Empty;
                                                 +    private string Message { get; set; } = "Hello";
                                                  }
                                                 """, "Update the page header markup.");

        Assert.Equal(RazorStructureRegion.CodeRegion, Assert.Single(blocked.Hunks).Region);
        Assert.False(blocked.IsValid);
        Assert.Contains(blocked.Errors, error => error.Contains("CodeRegion", StringComparison.Ordinal));

        var allowed = RazorStructureGuard.Analyze("""
                                                 diff --git a/Example.razor b/Example.razor
                                                 --- a/Example.razor
                                                 +++ b/Example.razor
                                                 @@ -10,6 +10,7 @@
                                                  @code {
                                                 -    private string Message { get; set; } = string.Empty;
                                                 +    private string Message { get; set; } = "Hello";
                                                  }
                                                 """, "Update the code-behind logic for the page.");

        Assert.True(allowed.IsValid);
    }
}
