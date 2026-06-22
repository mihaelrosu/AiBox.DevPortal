using System.Net;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchEditOperationServiceTests
{
    private static CoderConsoleService CreateConsoleService(IConfiguration configuration)
    {
        var httpClient = new HttpClient(new StubHandler());
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(Directory.GetCurrentDirectory());
        var contextService = new LocalCoderContextService();
        var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);

        return new CoderConsoleService(
            httpClient,
            configuration,
            environment,
            patchEditOperationService,
            contextService,
            new PatchVerificationService(new AgentRunHistoryService(environment)),
            new SelectedContextValidator(),
            new PatchPreviewRepairService(new StubOllamaService(), patchEditOperationService, configuration),
            new AgentInstructionService(environment));
    }

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
        var service = CreateConsoleService(configuration);

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

    [Fact]
    public async Task BuildAsync_RawJson_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-raw-json-test-{Guid.NewGuid():N}");
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
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = relativePath,
                            operation = "insert_after",
                            anchor = "public class Example {}",
                            newText = "\n// Added"
                        }
                    }
                }));

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

    [Fact]
    public async Task BuildAsync_TextBeforeJson_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-json-prefix-test-{Guid.NewGuid():N}");
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
                Here is the patch preview:

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

    [Fact]
    public async Task BuildAsync_TextAfterJson_ParsesOperations()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-json-suffix-test-{Guid.NewGuid():N}");
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

                Thanks.
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

    [Fact]
    public async Task BuildAsync_XmlDocRequest_ClassWithoutXmlDocs_SuggestsClassDeclaration()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-xml-doc-class-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = """
        public sealed class Example
        {
            public string Name { get; set; }
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
                                oldText = "<summary></summary>",
                                newText = "/// <summary></summary>"
                            }
                        }
                    })));

            var replaceDiagnostic = Assert.Single(exception.ReplaceDiagnostics);
            Assert.Contains("public sealed class Example", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);
            Assert.DoesNotContain("Name", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);

            var targetDiagnostic = Assert.Single(exception.SuggestedTargetDiagnostics);
            Assert.Equal("For adding documentation, use insert_before with the class or member declaration as anchor.", targetDiagnostic.Recommendation);
            Assert.Contains("public sealed class Example", targetDiagnostic.SuggestedTargetText, StringComparison.Ordinal);
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
    public async Task BuildAsync_XmlDocRequest_ClassWithXmlDocs_SuggestsSummaryBlock()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-xml-doc-block-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = """
        /// <summary>
        /// Existing docs
        /// </summary>
        public sealed class Example
        {
            public string Name { get; set; }
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
                                oldText = "<summary></summary>",
                                newText = "/// <summary>Updated docs</summary>"
                            }
                        }
                    })));

            var replaceDiagnostic = Assert.Single(exception.ReplaceDiagnostics);
            Assert.Contains("/// <summary>", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);
            Assert.Contains("Existing docs", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);

            var targetDiagnostic = Assert.Single(exception.SuggestedTargetDiagnostics);
            Assert.Contains("/// <summary>", targetDiagnostic.SuggestedTargetText, StringComparison.Ordinal);
            Assert.Equal("For adding documentation, use insert_before with the class or member declaration as anchor.", targetDiagnostic.Recommendation);
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
    public async Task BuildAsync_XmlDocRequest_DoesNotPreferPropertyOverDeclaration()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-xml-doc-property-test-{Guid.NewGuid():N}");
        const string relativePath = "Example.cs";
        const string content = """
        public sealed class Example
        {
            public string Name { get; set; }

            public string Title { get; set; }
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
                                oldText = "<summary></summary>",
                                newText = "/// <summary>Updated docs</summary>"
                            }
                        }
                    })));

            var replaceDiagnostic = Assert.Single(exception.ReplaceDiagnostics);
            Assert.DoesNotContain("Name { get; set; }", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);
            Assert.DoesNotContain("Title { get; set; }", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);
            Assert.Contains("public sealed class Example", replaceDiagnostic.SuggestedReplacementTarget, StringComparison.Ordinal);
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
    [InlineData("</summary>", "public static class GeneratedClass")]
    [InlineData("</summary>", "   public static class GeneratedClass")]
    [InlineData("</returns>", "public static string GetValue() => string.Empty;")]
    [InlineData("</returns>", "   public static string GetValue() => string.Empty;")]
    public async Task BuildAsync_XmlDocRequest_GluedDeclarationOnSameLine_IsNormalized(
        string closingTag,
        string declarationLine)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-xml-doc-formatting-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/Existing.cs";
        const string selectedContent = """
        public sealed class Existing
        {
            public static string GetValue() => string.Empty;
        }
        """;
        const string oldText = "    public static string GetValue() => string.Empty;";
        var newText = string.Join(
            Environment.NewLine,
            string.Empty,
            string.Empty,
            "/// <summary>",
            "/// Example documentation.",
            $"/// {closingTag}{declarationLine}");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = selectedRelativePath,
                            operation = "replace",
                            anchor = string.Empty,
                            oldText,
                            newText
                        }
                    }
                }));

            var change = Assert.Single(result.FileChanges);
            var expectedBlock = string.Join(
                Environment.NewLine,
                string.Empty,
                "    /// <summary>",
                "    /// Example documentation.",
                $"    /// {closingTag}",
                $"    {declarationLine.TrimStart()}") + Environment.NewLine;
            Assert.Contains(expectedBlock, change.NewContent, StringComparison.Ordinal);
            Assert.DoesNotContain($"{closingTag}{declarationLine}", change.NewContent, StringComparison.Ordinal);
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
    public async Task BuildAsync_CreateInRepresentedFolder_ProducesPatch()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-represented-folder-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string createdRelativePath = "Models/ProjectKnowledgeIndex.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string createdContent = "public sealed class ProjectKnowledgeIndex {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Task = $"Create {createdRelativePath}",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = selectedRelativePath,
                        Content = selectedContent
                    }
                ]
            });
            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = createdRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = createdContent
                        }
                    }
                }),
                intent);

            var change = Assert.Single(result.FileChanges);
            Assert.Equal(createdRelativePath, change.RelativePath);
            Assert.Equal(string.Empty, change.OldContent);
            Assert.Equal(createdContent, change.NewContent);
            Assert.Contains("diff --git a/Models/ProjectKnowledgeIndex.cs b/Models/ProjectKnowledgeIndex.cs", result.PatchText, StringComparison.Ordinal);
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
    public async Task BuildAsync_CreateFileScopedNamespace_PreservesBlankLineAfterNamespace()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-namespace-spacing-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string createdRelativePath = "Models/TaskSliceExecutionRequest.cs";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = createdRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = "namespace AiBox.DevPortal.Models;\r\n\tpublic sealed class TaskSliceExecutionRequest\r\n{\r\n}\r\n"
                        }
                    }
                }),
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath, createdRelativePath],
                    TargetCreatedFiles = [createdRelativePath],
                    AllowedCreateFolders = ["Models/"]
                });

            var change = Assert.Single(result.FileChanges);
            Assert.Contains(
                "namespace AiBox.DevPortal.Models;\r\n\r\npublic sealed class TaskSliceExecutionRequest",
                change.NewContent,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "namespace AiBox.DevPortal.Models;\r\n\tpublic sealed class TaskSliceExecutionRequest",
                change.NewContent,
                StringComparison.Ordinal);
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
    public async Task BuildAsync_CreateAllowedByIntent_ProducesPatch()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-allowed-by-intent-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string createdRelativePath = "Models/ProjectKnowledgeIndex.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string createdContent = "public sealed class ProjectKnowledgeIndex {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var logger = new ListLogger<PatchEditOperationService>();
            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = createdRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = createdContent
                        }
                    }
                }),
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath, createdRelativePath],
                    TargetCreatedFiles = [createdRelativePath],
                    AllowedCreateFolders = ["Models/"]
                });

            var change = Assert.Single(result.FileChanges);
            Assert.Equal(createdRelativePath, change.RelativePath);
            Assert.Equal(string.Empty, change.OldContent);
            Assert.Equal(createdContent, change.NewContent);
            Assert.Contains(logger.Entries, entry => entry.Contains("Validating patch operation.", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("filePath: Models/ProjectKnowledgeIndex.cs", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("operation: create", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logger.Entries, entry => entry.Contains("oldText length: 0", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("summary:", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Skipping exact-target and oldText validation", StringComparison.Ordinal));
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
    public async Task BuildAsync_MultipleCreateOperations_ProducesPatch()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-multiple-create-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string firstCreatedRelativePath = "Services/ProjectKnowledgeIndexService.cs";
        const string secondCreatedRelativePath = "Components/Coder/CoderKnowledgePanel.razor";
        const string firstCreatedContent = "public sealed class ProjectKnowledgeIndexService {}";
        const string secondCreatedContent = "<RadzenPanel />\n";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Coder"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var logger = new ListLogger<PatchEditOperationService>();
            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = firstCreatedRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = firstCreatedContent,
                            summary = "Create project knowledge service"
                        },
                        new
                        {
                            filePath = secondCreatedRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = secondCreatedContent,
                            summary = "Create knowledge panel"
                        }
                    }
                }),
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath, firstCreatedRelativePath, secondCreatedRelativePath],
                    TargetCreatedFiles = [firstCreatedRelativePath, secondCreatedRelativePath],
                    AllowedCreateFolders = ["Services/", "Components/Coder/"]
                });

            Assert.Equal(2, result.FileChanges.Count);
            Assert.Contains(result.FileChanges, change => change.RelativePath == firstCreatedRelativePath && change.NewContent == firstCreatedContent);
            Assert.Contains(result.FileChanges, change => change.RelativePath == secondCreatedRelativePath && change.NewContent == secondCreatedContent);
            Assert.Equal(2, logger.Entries.Count(entry => entry.Contains("Validating patch operation.", StringComparison.Ordinal)));
            Assert.Contains(logger.Entries, entry => entry.Contains("filePath: Services/ProjectKnowledgeIndexService.cs", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("filePath: Components/Coder/CoderKnowledgePanel.razor", StringComparison.Ordinal));
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
    public async Task BuildAsync_SingleCreateOperation_ParsesAndValidates()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-single-create-parse-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string createdRelativePath = "Services/TestService.cs";
        const string createdContent = "public sealed class TestService {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var logger = new ListLogger<PatchEditOperationService>();
            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                """
                {
                  "operations": [
                    {
                      "filePath": "Services/TestService.cs",
                      "operation": "create",
                      "newText": "public sealed class TestService {}",
                      "summary": "Create test service"
                    }
                  ]
                }
                """,
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath, createdRelativePath],
                    TargetCreatedFiles = [createdRelativePath],
                    AllowedCreateFolders = ["Services/"]
                });

            var change = Assert.Single(result.FileChanges);
            Assert.Equal(createdRelativePath, change.RelativePath);
            Assert.Equal(string.Empty, change.OldContent);
            Assert.Equal(createdContent, change.NewContent);
            Assert.Contains(logger.Entries, entry => entry.Contains("Patch preview parsed operation count: 1", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Patch preview parsed operation types: create", StringComparison.OrdinalIgnoreCase));
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
    public async Task BuildAsync_CreateOnlyWithoutSelectedContext_RespectsAllowedCreateFolders()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-only-no-context-test-{Guid.NewGuid():N}");
        const string firstCreatedRelativePath = "Models/TestFeature.cs";
        const string secondCreatedRelativePath = "Services/TestFeatureService.cs";
        const string firstCreatedContent = "public sealed class TestFeature {}";
        const string secondCreatedContent = "public sealed class TestFeatureService {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());
            var result = await service.BuildAsync(
                projectRoot,
                [],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = firstCreatedRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = firstCreatedContent
                        },
                        new
                        {
                            filePath = secondCreatedRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = secondCreatedContent
                        }
                    }
                }),
                new PatchIntent
                {
                    AllowedCreateFolders = ["Models/", "Services/"]
                });

            Assert.Equal(2, result.FileChanges.Count);
            Assert.Contains(result.FileChanges, change => change.RelativePath == firstCreatedRelativePath && change.NewContent == firstCreatedContent);
            Assert.Contains(result.FileChanges, change => change.RelativePath == secondCreatedRelativePath && change.NewContent == secondCreatedContent);
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
    public async Task BuildAsync_CreateOnlyWithoutSelectedContext_OutsideAllowedCreateFoldersFails()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-only-no-context-outside-folder-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "Components/TestFeature.razor",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "<RadzenPanel />"
                            }
                        }
                    }),
                    new PatchIntent
                    {
                        AllowedCreateFolders = ["Models/", "Services/"]
                    }));

            Assert.Contains(
                "create folder not allowed",
                string.Join(Environment.NewLine, exception.ValidationErrors),
                StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildAsync_UpdateWithoutSelectedContext_StillFails()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-update-no-context-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Models/TestFeature.cs"), "public sealed class TestFeature {}");

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "Models/TestFeature.cs",
                                operation = "replace",
                                anchor = string.Empty,
                                oldText = "public sealed class TestFeature {}",
                                newText = "public sealed class TestFeatureV2 {}"
                            }
                        }
                    }),
                    new PatchIntent
                    {
                        AllowedCreateFolders = ["Models/"]
                    }));

            Assert.Contains(
                "Patch preview requires selected file context.",
                string.Join(Environment.NewLine, exception.ValidationErrors),
                StringComparison.Ordinal);
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
    public async Task BuildAsync_MultipleCreateOperations_ParsesAndValidates()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-multiple-create-parse-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string firstCreatedRelativePath = "Services/ProjectKnowledgeIndexService.cs";
        const string secondCreatedRelativePath = "Components/Coder/CoderKnowledgePanel.razor";
        const string firstCreatedContent = "public sealed class ProjectKnowledgeIndexService {}";
        const string secondCreatedContent = "<RadzenPanel />\n";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Coder"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var logger = new ListLogger<PatchEditOperationService>();
            var service = new PatchEditOperationService(logger);
            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                """
                {
                  "operations": [
                    {
                      "filePath": "Services/ProjectKnowledgeIndexService.cs",
                      "operation": "create",
                      "newText": "public sealed class ProjectKnowledgeIndexService {}",
                      "summary": "Create project knowledge service"
                    },
                    {
                      "filePath": "Components/Coder/CoderKnowledgePanel.razor",
                      "operation": "create",
                      "newText": "<RadzenPanel />\n",
                      "summary": "Create knowledge panel"
                    }
                  ]
                }
                """,
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath, firstCreatedRelativePath, secondCreatedRelativePath],
                    TargetCreatedFiles = [firstCreatedRelativePath, secondCreatedRelativePath],
                    AllowedCreateFolders = ["Services/", "Components/Coder/"]
                });

            Assert.Equal(2, result.FileChanges.Count);
            Assert.Contains(result.FileChanges, change => change.RelativePath == firstCreatedRelativePath && change.NewContent == firstCreatedContent);
            Assert.Contains(result.FileChanges, change => change.RelativePath == secondCreatedRelativePath && change.NewContent == secondCreatedContent);
            Assert.Contains(logger.Entries, entry => entry.Contains("Patch preview parsed operation count: 2", StringComparison.Ordinal));
            Assert.Contains(logger.Entries, entry => entry.Contains("Patch preview parsed operation types: create, create", StringComparison.OrdinalIgnoreCase));
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
    public async Task BuildAsync_CreateAllowedByIntent_WithoutTargetCreatedFiles_ProducesPatch()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-allowed-without-target-created-files-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";
        const string createdRelativePath = "Models/ProjectKnowledgeIndex.cs";
        const string createdContent = "public sealed class ProjectKnowledgeIndex {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var result = await service.BuildAsync(
                projectRoot,
                [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    operations = new[]
                    {
                        new
                        {
                            filePath = createdRelativePath,
                            operation = "create",
                            anchor = string.Empty,
                            oldText = string.Empty,
                            newText = createdContent
                        }
                    }
                }),
                new PatchIntent
                {
                    AllowedFiles = [selectedRelativePath],
                    TargetCreatedFiles = [],
                    AllowedCreateFolders = ["Models/"]
                });

            var change = Assert.Single(result.FileChanges);
            Assert.Equal(createdRelativePath, change.RelativePath);
            Assert.Equal(createdContent, change.NewContent);
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
    public async Task BuildAsync_CreateInUnrepresentedFolder_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-unrepresented-folder-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "Services/ProjectKnowledgeIndexService.cs",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "public sealed class ProjectKnowledgeIndexService {}"
                            }
                        }
                    }),
                    new PatchIntent
                    {
                        AllowedFiles = [selectedRelativePath, "Services/ProjectKnowledgeIndexService.cs"],
                        TargetCreatedFiles = ["Services/ProjectKnowledgeIndexService.cs"],
                        AllowedCreateFolders = ["Models/"]
                    }));

            Assert.Contains(
                "create folder not allowed",
                string.Join(Environment.NewLine, exception.ValidationErrors),
                StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildAsync_CreateOutsideAllowedFolder_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-outside-represented-folder-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "Services/LocalCoderProjectKnowledgeIndexService.cs",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "public sealed class LocalCoderProjectKnowledgeIndexService {}"
                            }
                        }
                    })));

            Assert.Contains("create folder not allowed", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildAsync_CreateInKnowledgeFolderNotAllowed_ShowsExplicitFolderGuidance()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-knowledge-folder-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/CodexTaskExportResult.cs";
        const string selectedContent = "public sealed class CodexTaskExportResult {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Knowledge"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "Components/Knowledge/KnowledgeIndexPanel.razor",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "<RadzenCard />"
                            }
                        }
                    }),
                    new PatchIntent
                    {
                        AllowedFiles = [selectedRelativePath, "Components/Knowledge/KnowledgeIndexPanel.razor"],
                        TargetCreatedFiles = ["Components/Knowledge/KnowledgeIndexPanel.razor"],
                        AllowedCreateFolders = ["Models/"]
                    }));

            Assert.Contains(
                "Create folder not allowed. Add Components/Knowledge/ to Allowed Create Folders.",
                string.Join(Environment.NewLine, exception.ValidationErrors),
                StringComparison.Ordinal);
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
    public async Task BuildAsync_CreateOutsideProject_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-outside-project-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/LocalCoderHistoryEntry.cs";
        const string selectedContent = "public sealed class LocalCoderHistoryEntry {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "../SiblingProject/Models/ProjectKnowledgeIndex.cs",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "public sealed class ProjectKnowledgeIndex {}"
                            }
                        }
                    })));

            Assert.Contains(
                "invalid",
                string.Join(Environment.NewLine, exception.ValidationErrors),
                StringComparison.OrdinalIgnoreCase);
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
    public async Task BuildAsync_MalformedJson_FailsJsonValidation()
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
                    This is not valid JSON.
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

    [Fact]
    public async Task BuildAsync_CreateInBinFolder_FailsValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-bin-folder-test-{Guid.NewGuid():N}");
        const string selectedRelativePath = "Models/CodexTaskExportResult.cs";
        const string selectedContent = "public sealed class CodexTaskExportResult {}";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "bin"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), selectedContent);

            var service = new PatchEditOperationService(new ListLogger<PatchEditOperationService>());

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() =>
                service.BuildAsync(
                    projectRoot,
                    [new LocalCoderFileContext { RelativePath = selectedRelativePath, Content = selectedContent }],
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        operations = new[]
                        {
                            new
                            {
                                filePath = "bin/Generated.cs",
                                operation = "create",
                                anchor = string.Empty,
                                oldText = string.Empty,
                                newText = "public sealed class Generated {}"
                            }
                        }
                    }),
                    new PatchIntent
                    {
                        AllowedFiles = [selectedRelativePath, "bin/Generated.cs"],
                        TargetCreatedFiles = ["bin/Generated.cs"],
                        AllowedCreateFolders = ["bin/"]
                    }));

            Assert.Contains("blocked", string.Join(Environment.NewLine, exception.ValidationErrors), StringComparison.OrdinalIgnoreCase);
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

internal sealed class StubOllamaService : IOllamaService
{
    public Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ServiceHealth("Ollama", "stub", true, "Online"));
    }

    public Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(string.Empty);
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
