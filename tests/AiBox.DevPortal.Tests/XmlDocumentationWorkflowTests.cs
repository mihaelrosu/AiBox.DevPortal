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
    public void BuildGeneratePatchPreviewPrompt_XmlDocumentationModeAllowsReplaceOrInsertBefore()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Update xml documentation comments",
            Model = "test-model"
        };

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Example.cs",
            "FILE: Example.cs",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: true);

        Assert.Contains("XML documentation mode is active.", prompt, StringComparison.Ordinal);
        Assert.Contains("If XML documentation already exists, use replace with exact oldText.", prompt, StringComparison.Ordinal);
        Assert.Contains("If XML documentation does not exist, use insert_before with the exact class or member declaration as anchor.", prompt, StringComparison.Ordinal);
        Assert.Contains("Document every class, public member, and private method in the selected file unless it already has XML documentation.", prompt, StringComparison.Ordinal);
        Assert.Contains("For each undocumented declaration, emit one insert_before operation.", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not stop after documenting the first declaration.", prompt, StringComparison.Ordinal);
        Assert.Contains("Each insert_before anchor must be the exact declaration line from the selected context.", prompt, StringComparison.Ordinal);
        Assert.Contains("Anchor text must be copied verbatim from the selected file context.", prompt, StringComparison.Ordinal);
        Assert.Contains("Never reconstruct declarations.", prompt, StringComparison.Ordinal);
        Assert.Contains("Never append '{' to the anchor unless '{' appears on the same line in the selected context.", prompt, StringComparison.Ordinal);
        Assert.Contains("Use the entire declaration line exactly as shown.", prompt, StringComparison.Ordinal);
        Assert.Contains("If exact declaration text cannot be identified, emit no operation.", prompt, StringComparison.Ordinal);
        Assert.Contains("Each newText must end with a newline.", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not include the declaration in newText. Only include the XML documentation comment.", prompt, StringComparison.Ordinal);
        Assert.Contains("This applies to brace-on-next-line declarations, brace-on-same-line declarations, generic return types, nullable return types, and primary constructor class declarations.", prompt, StringComparison.Ordinal);
        Assert.Contains("<existing file>", prompt, StringComparison.Ordinal);
        Assert.Contains("<existing declaration>", prompt, StringComparison.Ordinal);
        Assert.Contains("<new content>", prompt, StringComparison.Ordinal);
        Assert.Contains("\"operation\": \"insert_before\"", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Models/ProjectKnowledgeIndex.cs", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Components/Pages/Coder.razor", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Generate replace operations only.", prompt, StringComparison.Ordinal);
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
            "- Example.cs",
            "FILE: Example.cs",
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
        Assert.Contains("<target file>", prompt, StringComparison.Ordinal);
        Assert.Contains("<existing file>", prompt, StringComparison.Ordinal);
        Assert.Contains("<existing anchor>", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not copy file paths from examples. Use only Allowed files and Target created file(s).", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Models/ProjectKnowledgeIndex.cs", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Components/Pages/Coder.razor", prompt, StringComparison.Ordinal);
        Assert.Contains("If exact oldText or anchor cannot be found, return:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_CreateTaskUsesCreateFriendlyContextInstruction()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Create Models/ProjectKnowledgeIndex.cs",
            Model = "test-model"
        };

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Models/LocalCoderHistoryEntry.cs",
            "FILE: Models/LocalCoderHistoryEntry.cs",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: false);

        Assert.Contains("Modify only selected context files. New files may be created only when explicitly requested and inside Allowed Create Folders.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Use only files from the selected file context.", prompt, StringComparison.Ordinal);
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
                    [new PatchSuggestedTargetMatch(1, "<h1>Header</h1>", 97.0)],
                    "For adding documentation, use insert_before with the class or member declaration as anchor.")
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
    public void BuildGeneratePatchPreviewPrompt_MalformedReplaceRepairAddsReplaceOnlyInstructions()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Read file and add XML documentation",
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
            "Read file and add XML documentation",
            """
            {
              "operations": [
                {
                  "filePath": "Components/Pages/Coder.razor",
                  "operation": "replace",
                  "oldText": "",
                  "newText": "<summary>Docs</summary>"
                }
              ]
            }
            """,
            ["Operation 'replace' for file 'Components/Pages/Coder.razor' requires a non-empty oldText."],
            [],
            []);

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Components/Pages/Coder.razor",
            "FILE: Components/Pages/Coder.razor",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: false,
            repairContext: repairContext);

        Assert.Contains("Replace operations require oldText.", prompt, StringComparison.Ordinal);
        Assert.Contains("Regenerate using exact oldText from the selected file context.", prompt, StringComparison.Ordinal);
        Assert.Contains("If a valid anchor is available, you may convert the operation to insert_before or insert_after.", prompt, StringComparison.Ordinal);
        Assert.Contains("Return JSON only.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPatchPromptAudit_CapturesContextSlices()
    {
        var fileContexts = new[]
        {
            new LocalCoderFileContext
            {
                RelativePath = "Components/Pages/Coder.razor",
                Content = "Alpha"
            },
            new LocalCoderFileContext
            {
                RelativePath = "Models/LocalCoderHistoryEntry.cs",
                Content = "BetaBeta"
            }
        };

        var fileContextText = "FIRST" + new string('A', 520) + "LAST";

        var audit = CoderConsoleService.BuildPatchPromptAudit(fileContexts, fileContextText);

        Assert.Equal(2, audit.ContextFiles.Count);
        Assert.Contains("Components/Pages/Coder.razor", audit.ContextFiles, StringComparer.Ordinal);
        Assert.Contains("Models/LocalCoderHistoryEntry.cs", audit.ContextFiles, StringComparer.Ordinal);
        Assert.Equal(13, audit.ContextCharactersLoaded);
        Assert.Equal(fileContextText.Length, audit.ContextTextCharactersSent);
        Assert.StartsWith("FIRST", audit.ContextTextFirst500Chars, StringComparison.Ordinal);
        Assert.DoesNotContain("LAST", audit.ContextTextFirst500Chars, StringComparison.Ordinal);
        Assert.EndsWith("LAST", audit.ContextTextLast500Chars, StringComparison.Ordinal);
        Assert.DoesNotContain("FIRST", audit.ContextTextLast500Chars, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_IncludesDeterministicTargetResolution()
    {
        var request = new LocalCoderRequest
        {
            ProjectPath = "/repo",
            Task = "Insert '// TEST_MARKER' immediately before: public sealed class LocalCoderHistoryEntry",
            Model = "test-model",
            FileContexts =
            [
                new LocalCoderFileContext
                {
                    RelativePath = "Models/LocalCoderHistoryEntry.cs",
                    Content = "public sealed class LocalCoderHistoryEntry {}"
                }
            ]
        };

        var targetResolution = new PatchPromptTargetResolution
        {
            TargetFound = true,
            Operation = "insert_before",
            FilePath = "Models/LocalCoderHistoryEntry.cs",
            LineNumber = 1,
            TargetText = "public sealed class LocalCoderHistoryEntry {}",
            SurroundingContext = "1: public sealed class LocalCoderHistoryEntry {}",
            MatchCount = 1
        };

        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            request,
            "- Models/LocalCoderHistoryEntry.cs",
            "FILE: Models/LocalCoderHistoryEntry.cs",
            "Context Files Only",
            "goal text",
            xmlDocumentationMode: false,
            targetResolution: targetResolution);

        Assert.Contains("Server-resolved target:", prompt, StringComparison.Ordinal);
        Assert.Contains("Target found: Yes", prompt, StringComparison.Ordinal);
        Assert.Contains("File: Models/LocalCoderHistoryEntry.cs", prompt, StringComparison.Ordinal);
        Assert.Contains("Line: 1", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not search for anchors.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateXmlDocumentationOperationModes_AllowsInsertBeforeWhenDocsAreMissing()
    {
        var rawResponse = """
        {
          "operations": [
            {
              "filePath": "Components/Pages/Coder.razor",
              "operation": "insert_before",
              "anchor": "public sealed class LocalCoderHistoryEntry",
              "newText": "/// <summary>\n/// Docs\n/// </summary>\n"
            }
          ]
        }
        """;

        CoderConsoleService.ValidateXmlDocumentationOperationModes(rawResponse);
    }

    [Fact]
    public void ValidateXmlDocumentationOperationModes_RejectsUnsupportedOperations()
    {
        var rawResponse = """
        {
          "operations": [
            {
              "filePath": "Components/Pages/Coder.razor",
              "operation": "insert_after",
              "anchor": "public sealed class LocalCoderHistoryEntry",
              "newText": "/// <summary>\n/// Docs\n/// </summary>\n"
            }
          ]
        }
        """;

        var exception = Assert.Throws<PatchPreviewValidationException>(() =>
            CoderConsoleService.ValidateXmlDocumentationOperationModes(rawResponse));

        Assert.Contains("replace or insert_before operations", exception.Message, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains("replace or insert_before operations", exception.Message, StringComparison.OrdinalIgnoreCase);
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
