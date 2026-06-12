using AiBox.DevPortal.Models;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchValidationGuidanceFactoryTests
{
    [Theory]
    [InlineData("Patch preview may break Razor markup by changing closing tag type.", PatchValidationCategory.RazorMarkupRisk)]
    [InlineData("Patch modifies protected Razor region: DocumentationRegion", PatchValidationCategory.RazorMarkupRisk)]
    [InlineData("Patch preview only changes closing tags.", PatchValidationCategory.HtmlStructureRisk)]
    [InlineData("Anchor not found for insert_after in file 'Example.razor': <p>Text</p>", PatchValidationCategory.MissingAnchor)]
    [InlineData("Anchor is ambiguous because it has multiple occurrences.", PatchValidationCategory.AmbiguousAnchor)]
    [InlineData("Patch edit operation file path is outside the project root: ../Example.razor", PatchValidationCategory.UnsafeFileOperation)]
    public void Create_ClassifiesValidationError(string error, PatchValidationCategory expectedCategory)
    {
        var guidance = Assert.Single(PatchValidationGuidanceFactory.Create([error]));

        Assert.Equal(expectedCategory, guidance.Category);
        Assert.Equal(error, guidance.Reason);
        Assert.False(string.IsNullOrWhiteSpace(guidance.SuggestedFix));
        Assert.False(string.IsNullOrWhiteSpace(guidance.OriginalOperation));
        Assert.False(string.IsNullOrWhiteSpace(guidance.SaferOperation));
    }

    [Fact]
    public void Create_HtmlStructureRisk_SuggestsCompleteBoundaries()
    {
        var guidance = Assert.Single(PatchValidationGuidanceFactory.Create(["Patch preview only changes closing tags."]));

        Assert.Contains("insert_after \"</p>\"", guidance.SuggestedFix, StringComparison.Ordinal);
        Assert.Contains("replace the exact paragraph block", guidance.SuggestedFix, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RazorMarkupRisk_SuggestsSaferReplacement()
    {
        var guidance = Assert.Single(PatchValidationGuidanceFactory.Create(["Patch preview may break Razor markup by changing closing tag type."]));

        Assert.Equal(PatchValidationCategory.RazorMarkupRisk, guidance.Category);
        Assert.Equal("insert_after <h1>", guidance.OriginalOperation);
        Assert.Equal("replace complete header block", guidance.SaferOperation);
    }

    [Fact]
    public void Create_ProtectedRazorRegion_SuggestsVisibleMarkup()
    {
        var guidance = Assert.Single(PatchValidationGuidanceFactory.Create(["Patch modifies protected Razor region: DocumentationRegion"]));

        Assert.Equal(PatchValidationCategory.RazorMarkupRisk, guidance.Category);
        Assert.Contains("Target the visible page markup section instead.", guidance.SuggestedFix, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationException_CreatesGuidanceWithoutChangingErrors()
    {
        string[] errors = ["Anchor not found for insert_after in file 'Example.razor': <p>Text</p>"];

        var exception = new PatchPreviewValidationException("Rejected", errors, "raw", "diff");

        Assert.Equal(errors, exception.ValidationErrors);
        Assert.Equal(PatchValidationCategory.MissingAnchor, Assert.Single(exception.Guidance).Category);
    }
}
