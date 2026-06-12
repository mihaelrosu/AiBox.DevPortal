using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Models.Repositories;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class XmlDocumentationWorkflowTests
{
    [Fact]
    public void BuildPlanningPrompt_UsesCodeChangeOnlyInstructions()
    {
        var task = new LocalCoderTask
        {
            Title = "Improve XML docs",
            RepositoryPath = "/repo",
            Model = "test-model",
            Instructions = "Add documentation comments.",
            AllowedPathsText = "Components/Pages/",
            ForbiddenPathsText = "bin/",
            BuildCommand = "dotnet build"
        };

        var prompt = LocalCoderService.BuildPlanningPrompt(
            task,
            new RepositoryScanResult
            {
                RepositoryPath = "/repo",
                Directories = ["Components"],
                Files = [new RepositoryFileSummary { RelativePath = "Components/Pages/Coder.razor" }]
            },
            [new RepositoryFileContent
            {
                RelativePath = "Components/Pages/Coder.razor",
                Content = "content",
                CharacterCount = 7
            }]);

        Assert.Contains("You must generate code-change tasks only.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("open file", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("navigate files", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDE", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_XmlDocumentationModeUsesReplaceOnlyInstructions()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Update xml documentation comments",
            Model = "test-model"
        };

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Components/Pages/Coder.razor",
            "FILE: Components/Pages/Coder.razor",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: true);

        Assert.Contains("XML documentation mode is active.", prompt, StringComparison.Ordinal);
        Assert.Contains("Generate replace operations only.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"operation\": \"insert_after\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"operation\": \"insert_before\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateXmlDocumentationOperationModes_RejectsNonReplaceOperations()
    {
        var rawResponse = """
        {
          "operations": [
            {
              "filePath": "Components/Pages/Coder.razor",
              "operation": "insert_after",
              "anchor": "<summary>",
              "newText": "<summary>Updated</summary>"
            }
          ]
        }
        """;

        var exception = Assert.Throws<PatchPreviewValidationException>(() =>
            CoderConsoleService.ValidateXmlDocumentationOperationModes(rawResponse));

        Assert.Contains("replace operations only", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XML documentation mode detected", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsXmlDocumentationRequest_DetectsCommonPhrases()
    {
        Assert.True(CoderConsoleService.IsXmlDocumentationRequest("Add xml documentation comments."));
        Assert.True(CoderConsoleService.IsXmlDocumentationRequest("Update xml comments."));
        Assert.True(CoderConsoleService.IsXmlDocumentationRequest("Fix documentation comments for the API."));
        Assert.False(CoderConsoleService.IsXmlDocumentationRequest("Update UI spacing."));
    }
}
