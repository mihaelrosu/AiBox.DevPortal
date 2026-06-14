using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class SelectedContextValidatorTests
{
    private readonly SelectedContextValidator validator = new();

    [Fact]
    public void ValidateForPatchPreview_IgnoresAgentsFiles()
    {
        var exception = Assert.Throws<PatchPreviewValidationException>(() => validator.ValidateForPatchPreview(
            [
                new LocalCoderFileContext { RelativePath = "AGENTS.md", Content = "# root" },
                new LocalCoderFileContext { RelativePath = "Components/AGENTS.md", Content = "# components" }
            ]));

        Assert.Equal("No editable source files selected and no valid create targets were detected.", exception.Message);
        Assert.Equal(["No editable source files selected."], exception.ValidationErrors);
    }

    [Fact]
    public void ValidateForPatchPreview_RejectsTruncatedEditableFiles()   {}     
    [Fact]
        public void ValidateForPatchPreview_AllowsCreateOnlyTargets()
        {
            validator.ValidateForPatchPreview(
                [
                    new LocalCoderFileContext { RelativePath = "Services/ProjectKnowledgeIndex.cs", Content = "public sealed class ProjectKnowledgeIndex {}" }
                ]);
        }
    {
        var exception = Assert.Throws<PatchPreviewValidationException>(() => validator.ValidateForPatchPreview(
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor", Content = "<div />", IsTruncated = true }
            ]));

        Assert.Equal("Selected file context is incomplete. Patch preview requires full file contents.", exception.Message);
        Assert.Equal(["Selected file context is incomplete. Patch preview requires full file contents."], exception.ValidationErrors);
    }

    [Fact]
    public void ValidateForPatchPreview_AllowsCompleteEditableFiles()
    {
        validator.ValidateForPatchPreview(
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor", Content = "<div />" }
            ]);
    }
}
