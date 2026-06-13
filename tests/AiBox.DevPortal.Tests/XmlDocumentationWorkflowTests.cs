using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Models.Repositories;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Xunit;
using AgentLocalCoderTask = AiBox.DevPortal.Models.Agents.LocalCoderTask;

namespace AiBox.DevPortal.Tests;

public sealed class XmlDocumentationWorkflowTests
{
    [Fact]
    public void BuildPlanningPrompt_UsesCodeChangeOnlyInstructions()
    {
        var task = new AgentLocalCoderTask
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
        Assert.Contains("replace:", prompt, StringComparison.Ordinal);
        Assert.Contains("oldText must be exact text from selected context", prompt, StringComparison.Ordinal);
        Assert.Contains("If exact oldText or anchor cannot be found, return:", prompt, StringComparison.Ordinal);
        Assert.Contains("\"errors\": [", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"operation\": \"insert_after\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"operation\": \"insert_before\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_NonXmlModeIncludesOperationRules()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Update the page header",
            Model = "test-model"
        };

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Components/Pages/Coder.razor",
            "FILE: Components/Pages/Coder.razor",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: false);

        Assert.Contains("Allowed operation grammar:", prompt, StringComparison.Ordinal);
        Assert.Contains("replace:", prompt, StringComparison.Ordinal);
        Assert.Contains("oldText required", prompt, StringComparison.Ordinal);
        Assert.Contains("newText required", prompt, StringComparison.Ordinal);
        Assert.Contains("insert_before:", prompt, StringComparison.Ordinal);
        Assert.Contains("anchor required", prompt, StringComparison.Ordinal);
        Assert.Contains("insert_after:", prompt, StringComparison.Ordinal);
        Assert.Contains("remove:", prompt, StringComparison.Ordinal);
        Assert.Contains("Forbidden examples:", prompt, StringComparison.Ordinal);
        Assert.Contains("invented anchors", prompt, StringComparison.Ordinal);
        Assert.Contains("code fences around JSON", prompt, StringComparison.Ordinal);
        Assert.Contains("If exact oldText or anchor cannot be found, return:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_RepairModeIncludesValidationPayload()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Fix the page header",
            Model = "test-model",
            FileContexts =
            [
                new LocalCoderFileContext
                {
                    RelativePath = "Components/Pages/Coder.razor",
                    Content = "<h1>Header</h1>"
                }
            ]
        };

        var repairContext = new PatchPreviewRepairContext(
            "Fix the page header",
            "{\"operations\":[]}",
            ["Old text not found for replace in file 'Components/Pages/Coder.razor': <h1>Headr</h1>"],
            [],
            [
                new PatchSuggestedTargetDiagnostic(
                    "Components/Pages/Coder.razor",
                    "replace",
                    "oldText",
                    "<h1>Headr</h1>",
                    "<h1>Header</h1>",
                    [new PatchSuggestedTargetMatch(1, "<h1>Header</h1>", 97.0)])
            ]);

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Components/Pages/Coder.razor",
            "FILE: Components/Pages/Coder.razor",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: false,
            repairContext: repairContext);

        Assert.Contains("Repair mode is active.", prompt, StringComparison.Ordinal);
        Assert.Contains("Return corrected patch JSON only.", prompt, StringComparison.Ordinal);
        Assert.Contains("Original user task:", prompt, StringComparison.Ordinal);
        Assert.Contains("Raw model response:", prompt, StringComparison.Ordinal);
        Assert.Contains("Validation errors:", prompt, StringComparison.Ordinal);
        Assert.Contains("Closest-match suggestions from diagnostics:", prompt, StringComparison.Ordinal);
        Assert.Contains("Patch operation grammar rules:", prompt, StringComparison.Ordinal);
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
    public void ValidateXmlDocumentationOperationModes_HandlesMarkdownFencedJson()
    {
        var rawResponse = """
        ```json
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
        ```
        """;

        var exception = Assert.Throws<PatchPreviewValidationException>(() =>
            CoderConsoleService.ValidateXmlDocumentationOperationModes(rawResponse));

        Assert.Contains("replace operations only", exception.Message, StringComparison.OrdinalIgnoreCase);
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
