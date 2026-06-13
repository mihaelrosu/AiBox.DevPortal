using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchEditOperationServiceTests
{
    [Fact]
    public async Task BuildAsync_HtmlAnchorFallsBackToRadzenText_ProducesPatchAndLogsStrategy()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-anchor-test-{Guid.NewGuid():N}");
        var relativePath = "Components/Pages/Coder.razor";
        var filePath = Path.Combine(projectRoot, relativePath);
        const string content = "<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H5\" />";
        var logger = new ListLogger<PatchEditOperationService>();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            await File.WriteAllTextAsync(filePath, content);

            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                """
                {
                  "operations": [
                    {
                      "filePath": "Components/Pages/Coder.razor",
                      "operation": "insert_after",
                      "anchor": "<h3>Patch Queue</h3>",
                      "newText": "\n<RadzenAlert />"
                    }
                  ]
                }
                """);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal($"{content}{Environment.NewLine}<RadzenAlert />", change.NewContent);
            Assert.Contains(logger.Entries, entry => entry.Contains("Original anchor: <h3>Patch Queue</h3>", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Normalized anchor: Patch Queue", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Match strategy: ExactText", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RealPatchQueuePanel_HtmlAnchorFallsBackToPatchQueueRadzenText()
    {
        var projectRoot = FindProjectRoot();
        const string relativePath = "Components/Coder/CoderPatchQueuePanel.razor";
        var content = await File.ReadAllTextAsync(Path.Combine(projectRoot, relativePath));
        var logger = new ListLogger<PatchEditOperationService>();

        var service = new PatchEditOperationService(logger);
        var result = await service.BuildAsync(
            projectRoot,
            [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
            """
            {
              "operations": [
                {
                  "filePath": "Components/Coder/CoderPatchQueuePanel.razor",
                  "operation": "insert_after",
                  "anchor": "<h3>Patch Queue</h3>",
                  "newText": "\n<!-- Patch Queue integration test -->"
                }
              ]
            }
            """);

        var change = Assert.Single(result.FileChanges);
        Assert.Contains(
            """
            <RadzenText Text="Patch Queue" TextStyle="TextStyle.H5" />
            <!-- Patch Queue integration test -->
            """,
            change.NewContent,
            StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Contains("Contains Patch Queue: True", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Contains("Patch Queue count: 1", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Contains("Match strategy: ExactText", StringComparison.Ordinal));
        Assert.StartsWith(
            "diff --git a/Components/Coder/CoderPatchQueuePanel.razor b/Components/Coder/CoderPatchQueuePanel.razor",
            result.PatchText,
            StringComparison.Ordinal);
        Assert.Contains("--- a/Components/Coder/CoderPatchQueuePanel.razor", result.PatchText, StringComparison.Ordinal);
        Assert.Contains("+++ b/Components/Coder/CoderPatchQueuePanel.razor", result.PatchText, StringComparison.Ordinal);
        Assert.DoesNotContain("CoderPatchQueuePanel.razorComponents", result.PatchText, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry =>
            entry.Contains("Original diff header:", StringComparison.Ordinal) &&
            entry.Contains("Parsed old path:", StringComparison.Ordinal) &&
            entry.Contains("Parsed new path:", StringComparison.Ordinal) &&
            entry.Contains("Normalized path: a/Components/Coder/CoderPatchQueuePanel.razor -> b/Components/Coder/CoderPatchQueuePanel.razor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadFileContextsAsync_RealPatchQueuePanel_PreservesCurrentPatchQueueContent()
    {
        var projectRoot = FindProjectRoot();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
            })
            .Build();
        var service = new CoderConsoleService(null!, configuration, null!, null!, new LocalCoderContextService());

        var contexts = await service.ReadFileContextsAsync(projectRoot, ["Components/Coder/CoderPatchQueuePanel.razor"]);

        var context = Assert.Single(contexts);
        var currentContent = await File.ReadAllTextAsync(Path.Combine(projectRoot, context.RelativePath));
        Assert.Equal(currentContent, context.Content);
        Assert.Contains("<RadzenText Text=\"Patch Queue\" TextStyle=\"TextStyle.H5\" />", context.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_JsonMarkdownFence_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-fenced-json-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        var filePath = Path.Combine(projectRoot, relativePath);
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                """
                ```json
                {
                  "operations": [
                    {
                      "filePath": "Example.cs",
                      "operation": "insert_after",
                      "anchor": "public class Example {}",
                      "newText": "\n// Added"
                    }
                  ]
                }
                ```
                """);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal($"{content}{Environment.NewLine}// Added", change.NewContent);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("@page \"/test\"")]
    [InlineData("@using System.Text")]
    [InlineData("@inject NavigationManager Navigation")]
    [InlineData("@attribute [Authorize]")]
    [InlineData("@layout MainLayout")]
    public async Task BuildAsync_InsertAfterRazorDirective_InsertsContentOnSeparatedLine(string directive)
    {
        var result = await BuildSingleFileOperationAsync(
            directive,
            directive,
            "@* Test Comment *@");

        var change = Assert.Single(result.FileChanges);
        Assert.Equal(
            $"{directive}{Environment.NewLine}{Environment.NewLine}@* Test Comment *@",
            change.NewContent);
    }

    [Fact]
    public async Task BuildAsync_InsertAfterRazorDirective_PreservesCrLfLineEndings()
    {
        const string directive = "@page \"/test\"";
        var content = $"{directive}\r\n<h1>Test</h1>\r\n";

        var result = await BuildSingleFileOperationAsync(
            content,
            directive,
            "@* Test Comment *@");

        var change = Assert.Single(result.FileChanges);
        Assert.Equal(
            $"{directive}\r\n\r\n@* Test Comment *@\r\n<h1>Test</h1>\r\n",
            change.NewContent);
    }

    [Fact]
    public async Task BuildAsync_UnclosedMarkdownFence_FailsJsonValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-malformed-fence-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = "Example.cs", Content = "content" }],
                    """
                    ```json
                    {
                      "operations": []
                    }
                    """));

            Assert.Contains("JSON parse error", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_MarkdownFencedJson_WithWrapperText_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-fenced-json-wrapper-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Example.cs"), "Hello");
            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = "Example.cs", Content = "Hello" }],
                """
                Here is the patch preview:

                ```json
                {
                  "operations": [
                    {
                      "filePath": "Example.cs",
                      "operation": "replace",
                      "anchor": "",
                      "oldText": "Hello",
                      "newText": "Hi"
                    }
                  ]
                }
                ```

                Thanks.
                """);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal("Hi", change.NewContent);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_ReplaceMissingOldText_ReportsClosestMatches()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-replace-diagnostics-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = """
        public class Example
        {
            public void AlphaBeta()
            {
            }
        }
        """;

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "replace",
                                anchor = string.Empty,
                                oldText = "public void AlphaBeto()",
                                newText = "public void AlphaBetaRenamed()"
                            }
                        }
                    })));

            var diagnostics = Assert.Single(exception.ReplaceDiagnostics);
            Assert.Equal(relativePath, diagnostics.FilePath);
            Assert.Equal("public void AlphaBeto()", diagnostics.RequestedOldText);
            Assert.NotEmpty(diagnostics.ClosestMatches);
            Assert.NotEmpty(diagnostics.SuggestedReplacementTarget);
            Assert.Contains(diagnostics.ClosestMatches, match => match.LineNumber == 3 && match.SimilarityScore > 50.0);

            var suggestedTarget = Assert.Single(exception.SuggestedTargetDiagnostics);
            Assert.Equal("replace", suggestedTarget.FailedOperation);
            Assert.Equal("oldText", suggestedTarget.TargetLabel);
            Assert.Equal("public void AlphaBeto()", suggestedTarget.RequestedText);
            Assert.NotEmpty(suggestedTarget.ClosestMatches);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_InsertAfterMissingAnchor_ReportsSuggestedTarget()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-anchor-suggested-target-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = """
        public class Example
        {
            public void Render()
            {
            }
        }
        """;

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "insert_after",
                                anchor = "Missing target",
                                oldText = string.Empty,
                                newText = "\n// Added"
                            }
                        }
                    })));

            var diagnostics = Assert.Single(exception.SuggestedTargetDiagnostics);
            Assert.Equal("insert_after", diagnostics.FailedOperation);
            Assert.Equal("anchor", diagnostics.TargetLabel);
            Assert.Equal("Missing target", diagnostics.RequestedText);
            Assert.NotEmpty(diagnostics.ClosestMatches);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveExactText_ProducesEmptyFile()
    {
        var result = await BuildSingleFileOperationAsync(
            "Hello",
            "Hello",
            string.Empty,
            "remove");

        var change = Assert.Single(result.FileChanges);
        Assert.Equal(string.Empty, change.NewContent);
        Assert.Contains("Hello", change.OldContent, StringComparison.Ordinal);
        Assert.Contains("diff --git a/Example.razor b/Example.razor", result.PatchText, StringComparison.Ordinal);
        Assert.Contains("-Hello", result.PatchText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RemoveRazorComment_PreservesLineEndings()
    {
        const string content = "Before\r\n@* Documentation placeholder *@\r\nAfter";
        var result = await BuildSingleFileOperationAsync(
            content,
            "@* Documentation placeholder *@",
            string.Empty,
            "remove");

        var change = Assert.Single(result.FileChanges);
        Assert.Equal("Before\r\nAfter", change.NewContent);
        Assert.Contains("-@* Documentation placeholder *@", result.PatchText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_RemoveEmptyOldText_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-empty-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), "Hello");

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = "Hello" }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "remove",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = string.Empty
                            }
                        }
                    })));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.Contains("requires a non-empty oldText", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveDuplicateOldTextWithoutAnchor_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-duplicate-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "Hello\nWorld\nHello";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "remove",
                                anchor = string.Empty,
                                oldText = "Hello",
                                newText = string.Empty
                            }
                        }
                    })));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.Contains("make the remove task more specific", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_InsertBeforeMissingAnchor_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-insert-before-anchor-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "insert_before",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "\n// Added"
                            }
                        }
                    })));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.Contains("requires a non-empty anchor", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_ReplaceMissingOldText_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-replace-empty-old-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "replace",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "public class ExampleRenamed {}"
                            }
                        }
                    })));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.Contains("requires a non-empty oldText", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveMissingOldText_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-empty-old-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "remove",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = string.Empty
                            }
                        }
                    })));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.Contains("requires a non-empty oldText", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_EmptyOperationsWithErrors_ReturnsFriendlyValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-empty-ops-errors-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "public class Example {}";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                    """
                    {
                      "operations": [],
                      "errors": [
                        "Exact target text was not found in context."
                      ]
                    }
                    """));

            Assert.Equal("The model returned an invalid patch operation.", exception.Message);
            Assert.True(
                exception.ValidationErrors.Any(error =>
                    string.Equals(error, "Exact target text was not found in context.", StringComparison.Ordinal)));
            Assert.True(
                exception.OperationGrammarErrors.Any(error =>
                    string.Equals(error, "Exact target text was not found in context.", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveDuplicateOldTextWithAnchor_ProducesPatch()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-anchor-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = "Start\nHello\nMiddle\nHello";

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, relativePath), content);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = relativePath,
                            operation = "remove",
                            anchor = "Middle",
                            oldText = "Hello",
                            newText = string.Empty
                        }
                    }
            }));

            var change = Assert.Single(result.FileChanges);
            Assert.Equal("Start\nMiddle\nHello", change.NewContent);
            Assert.StartsWith("diff --git a/Example.cs b/Example.cs", result.PatchText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveOverSafetyLimit_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-limit-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        var filePath = Path.Combine(projectRoot, relativePath);
        var oldText = new string('A', 10 * 1024 + 1);

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, oldText);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = oldText }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "remove",
                                anchor = string.Empty,
                                oldText,
                                newText = string.Empty
                            }
                        }
                    })));

            Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildAsync_RemoveWildcardPattern_IsRejected()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-wildcard-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        var filePath = Path.Combine(projectRoot, relativePath);

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "Hello");

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = relativePath, Content = "Hello" }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = relativePath,
                                operation = "remove",
                                anchor = string.Empty,
                                oldText = "He*lo",
                                newText = string.Empty
                            }
                        }
                    })));

            Assert.Contains("wildcard matching", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static async Task<PatchEditOperationResult> BuildSingleFileOperationAsync(
        string content,
        string anchor,
        string newText,
        string operation = "insert_after",
        string? oldText = null)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-directive-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.razor";
        var filePath = Path.Combine(projectRoot, relativePath);

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, content);

            var rawJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                operations = new[]
                {
                    new
                    {
                        filePath = relativePath,
                        operation,
                        anchor,
                        oldText = oldText ?? anchor,
                        newText
                    }
                }
            });

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            return await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = relativePath, Content = content }],
                rawJson);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static string FindProjectRoot()
    {
        foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Components", "Pages", "Coder.razor")) &&
                    File.Exists(Path.Combine(directory.FullName, "AiBox.DevPortal.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the AiBox.DevPortal project root.");
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
