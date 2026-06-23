using System.Net;
using System.Net.Http;
using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class CoderConsoleServiceRepairTests
{
    private static CoderConsoleService CreateService(
        HttpClient httpClient,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        IPatchEditOperationService patchEditOperationService,
        ILocalCoderContextService contextService)
    {
        var repairService = new PatchPreviewRepairService(
            new OllamaService(httpClient),
            patchEditOperationService,
            configuration);

        return new CoderConsoleService(
            httpClient,
            configuration,
            environment,
            patchEditOperationService,
            contextService,
            new PatchVerificationService(new AgentRunHistoryService(environment)),
            new SelectedContextValidator(),
            repairService,
            new AgentInstructionService(environment));
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_AutoRepairsMalformedReplaceOldText()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-auto-repair-test-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "public class Example {}");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"replace\",\"oldText\":\"\",\"newText\":\"// note\"}]}",
                  "done": true
                }
                """,
                """
                {
                  "response": "Here is the repaired patch:\n\n```json\n{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"insert_before\",\"anchor\":\"public class Example {}\",\"oldText\":\"\",\"newText\":\"// note\\n\"}]}\n```\n\nThanks.",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Read file and add comment",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = "public class Example {}"
                    }
                ]
            });

            Assert.NotNull(preview.RepairSummary);
            Assert.Equal("replace", preview.RepairSummary!.OriginalOperation);
            Assert.Equal("insert_before", preview.RepairSummary.RepairAttempt);
            Assert.Equal("Success", preview.RepairSummary.RepairResult);
            Assert.Contains("// note", preview.PatchText, StringComparison.Ordinal);
            Assert.Equal(2, requests.Count);
            Assert.Contains("Replace operations require oldText.", requests[1], StringComparison.Ordinal);
            Assert.Contains("exact oldText from the selected file context", requests[1], StringComparison.Ordinal);
            Assert.Contains("Return JSON only.", requests[1], StringComparison.Ordinal);
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
    public async Task GeneratePatchPreviewAsync_AutoRepairsMissingInsertAnchor()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-insert-repair-test-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, """
            public class Example
            {
                public void Render()
                {
                }
            }
            """);

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"insert_after\",\"anchor\":\"Missing target\",\"oldText\":\"\",\"newText\":\"\\n// note\"}]}",
                  "done": true
                }
                """,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"insert_after\",\"anchor\":\"public void Render()\",\"oldText\":\"\",\"newText\":\"\\n// note\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Add a comment after Render",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = await File.ReadAllTextAsync(filePath)
                    }
                ]
            });

            Assert.NotNull(preview.RepairSummary);
            Assert.Equal("insert_after", preview.RepairSummary!.OriginalOperation);
            Assert.Equal("insert_after", preview.RepairSummary.RepairAttempt);
            Assert.Equal("Success", preview.RepairSummary.RepairResult);
            Assert.Contains("// note", preview.PatchText, StringComparison.Ordinal);
            Assert.Equal(2, requests.Count);
            Assert.Contains("Insert operations require an exact anchor.", requests[1], StringComparison.Ordinal);
            Assert.Contains("closest-match suggestion as the anchor", requests[1], StringComparison.Ordinal);
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
    public async Task GeneratePatchPreviewAsync_AutoRepairsRemoveMissingOldText()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-remove-repair-test-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "public class Example {}");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"remove\",\"anchor\":\"\",\"oldText\":\"\",\"newText\":\"\"}]}",
                  "done": true
                }
                """,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"remove\",\"anchor\":\"\",\"oldText\":\"public class Example {}\",\"newText\":\"\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Remove the class",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = "public class Example {}"
                    }
                ]
            });

            Assert.NotNull(preview.RepairSummary);
            Assert.Equal("remove", preview.RepairSummary!.OriginalOperation);
            Assert.Equal("remove", preview.RepairSummary.RepairAttempt);
            Assert.Equal("Success", preview.RepairSummary.RepairResult);
            Assert.Contains("diff --git", preview.PatchText, StringComparison.Ordinal);
            Assert.Equal(2, requests.Count);
            Assert.Contains("Remove operations require oldText.", requests[1], StringComparison.Ordinal);
            Assert.Contains("exact oldText from the selected file context", requests[1], StringComparison.Ordinal);
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
    public async Task GeneratePatchPreviewAsync_FailedPreviewIncrementsFailureMetric()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-failed-metric-test-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "public class Example {}");

            var handler = new QueueHandler(
                new List<string>(),
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"rename\",\"oldText\":\"public class Example {}\",\"newText\":\"public class ExampleRenamed {}\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            await Assert.ThrowsAsync<PatchPreviewValidationException>(() => service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Rename the class",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = "public class Example {}"
                    }
                ]
            }));
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
    public async Task GeneratePatchPreviewAsync_CreateOnlyTask_AllowsValidCreateTargetsWithoutEditableContext()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-only-test-{Guid.NewGuid():N}");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "AGENTS.md"), "# root instructions");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Models/ProjectKnowledgeIndex.cs\",\"operation\":\"create\",\"oldText\":\"\",\"newText\":\"public sealed class ProjectKnowledgeIndex {}\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Create Models/ProjectKnowledgeIndex.cs",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "AGENTS.md",
                        Content = "# root instructions"
                    }
                ]
            });

            Assert.NotNull(preview);
            var change = Assert.Single(preview.FileChanges);
            Assert.Equal("Models/ProjectKnowledgeIndex.cs", change.RelativePath);
            Assert.Contains("public sealed class ProjectKnowledgeIndex {}", change.NewContent, StringComparison.Ordinal);
            Assert.NotNull(preview.IntentValidation);
            Assert.NotEqual(PatchIntentMatchStatus.DoesNotMatch, preview.IntentValidation!.Status);
            Assert.Single(requests);
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
    public async Task RepairAsync_CreateOnlyTask_PreservesCreateTargetsInPrompt()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-only-repair-test-{Guid.NewGuid():N}");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "AGENTS.md"), "# root instructions");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Models/TaskSliceExecutionRequest.cs\",\"operation\":\"create\",\"oldText\":\"\",\"newText\":\"public sealed class TaskSliceExecutionRequest {}\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var repairService = new PatchPreviewRepairService(new OllamaService(httpClient), patchEditOperationService, configuration);
            var request = new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = """
                Create:
                - Models/TaskSliceExecutionRequest.cs
                """,
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "AGENTS.md",
                        Content = "# root instructions"
                    }
                ]
            };
            var intent = PatchIntentService.BuildIntent(request);
            var repairContext = new PatchPreviewRepairContext(
                request.Task,
                "{\"operations\":[]}",
                ["Operation 'create' for file 'Models/TaskSliceExecutionRequest.cs' must include non-empty newText."],
                [],
                []);

            var result = await repairService.RepairAsync(
                request,
                intent,
                PatchIntentService.BuildPromptText(intent),
                "- AGENTS.md",
                "FILE: AGENTS.md\n# root instructions",
                "Context Files Only",
                xmlDocumentationMode: false,
                targetResolution: null,
                repairContext,
                string.Empty,
                "create",
                "create",
                null);

            Assert.True(result.Success);
            Assert.NotNull(result.RepairPrompt);
            Assert.Contains("Target created file(s):", result.RepairPrompt, StringComparison.Ordinal);
            Assert.Contains("Models/TaskSliceExecutionRequest.cs", result.RepairPrompt, StringComparison.Ordinal);
            Assert.Contains("Allowed files:", result.RepairPrompt, StringComparison.Ordinal);
            Assert.Contains("Allowed create folders:", result.RepairPrompt, StringComparison.Ordinal);
            Assert.Contains("Models/", result.RepairPrompt, StringComparison.Ordinal);
            Assert.Contains("public sealed class TaskSliceExecutionRequest {}", result.RepairRawResponse, StringComparison.Ordinal);
            Assert.Single(requests);
            Assert.Contains("Target created file(s):", requests[0], StringComparison.Ordinal);
            Assert.Contains("Models/TaskSliceExecutionRequest.cs", requests[0], StringComparison.Ordinal);
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
    public async Task GeneratePatchPreviewAsync_DeterministicTargetResolution_ReachesModelPrompt()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-deterministic-target-prompt-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "public class Example {}");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"insert_before\",\"anchor\":\"public class Example {}\",\"oldText\":\"\",\"newText\":\"// TEST_MARKER\\n\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Insert '// TEST_MARKER' immediately before: public class Example {}",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = "public class Example {}"
                    }
                ]
            });

            Assert.Single(requests);
            Assert.Contains("Server-resolved target:", requests[0], StringComparison.Ordinal);
            Assert.Contains("Target found: Yes", requests[0], StringComparison.Ordinal);
            Assert.Contains("public class Example {}", requests[0], StringComparison.Ordinal);
            Assert.Equal("insert_before", preview.PromptTargetResolution?.Operation);
            Assert.True(preview.PromptTargetResolution?.TargetFound);
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
    public async Task GeneratePatchPreviewAsync_DeterministicTargetMissing_StopsBeforeModelCall()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-deterministic-target-missing-{Guid.NewGuid():N}");
        var filePath = Path.Combine(projectRoot, "Example.cs");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(filePath, "public class Example {}");

            var handler = new QueueHandler(requests);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() => service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Insert '// TEST_MARKER' immediately before: public sealed class MissingType",
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Example.cs",
                        Content = "public class Example {}"
                    }
                ]
            }));

            Assert.Contains("Deterministic target text was not found in selected context", exception.Message, StringComparison.Ordinal);
            Assert.NotNull(exception.PromptTargetResolution);
            Assert.False(exception.PromptTargetResolution!.TargetFound);
            Assert.Equal("insert_before", exception.PromptTargetResolution.Operation);
            Assert.Empty(requests);
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
    public async Task GeneratePatchPreviewAsync_NoSelectedContext_StopsBeforeModelCall()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-no-selected-context-{Guid.NewGuid():N}");
        var requests = new List<string>();

        try
        {
            Directory.CreateDirectory(projectRoot);

            var handler = new QueueHandler(requests);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var exception = await Assert.ThrowsAsync<PatchPreviewValidationException>(() => service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = "Rename the class"
            }));

            Assert.Contains("No editable source files selected and no valid create targets were detected.", exception.Message, StringComparison.Ordinal);
            Assert.Empty(requests);
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
    public async Task GeneratePatchPreviewAsync_CreateAllowedFileInRepresentedFolder_PassesValidation()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-create-intent-preview-{Guid.NewGuid():N}");
        var requests = new List<string>();
        const string selectedRelativePath = "Models/CodexTaskExportResult.cs";
        const string createdRelativePath = "Models/ProjectKnowledgeIndex.cs";

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, selectedRelativePath), "public sealed class CodexTaskExportResult {}");

            var handler = new QueueHandler(
                requests,
                """
                {
                  "response": "{\"operations\":[{\"filePath\":\"Models/ProjectKnowledgeIndex.cs\",\"operation\":\"create\",\"oldText\":\"\",\"newText\":\"public sealed class ProjectKnowledgeIndex {}\"}]}",
                  "done": true
                }
                """);

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost")
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();

            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var preview = await service.GeneratePatchPreviewAsync(new LocalCoderRequest
            {
                ProjectPath = projectRoot,
                Model = "test-model",
                Task = $"Create {createdRelativePath}",
                AllowedCreateFolders = ["Models/"],
                FileContexts =
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = selectedRelativePath,
                        Content = "public sealed class CodexTaskExportResult {}"
                    }
                ]
            });

            Assert.Single(requests);
            Assert.Contains("Target created file(s):", requests[0], StringComparison.Ordinal);
            Assert.Contains("Allowed create folders:", requests[0], StringComparison.Ordinal);
            Assert.Contains(createdRelativePath, requests[0], StringComparison.Ordinal);
            Assert.NotNull(preview);
            Assert.Single(preview.FileChanges);
            Assert.Equal(createdRelativePath, preview.FileChanges[0].RelativePath);
            Assert.Contains("Models/ProjectKnowledgeIndex.cs", preview.PatchText, StringComparison.Ordinal);
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
    public async Task CreatePlanAsync_UsesProvidedChatRouteEndpoint()
    {
        var requests = new List<Uri>();
        var handler = new RouteCapturingHandler(
            requests,
            """
            {
              "choices": [
                {
                  "message": {
                    "role": "assistant",
                    "content": "Plan"
                  }
                }
              ]
            }
            """);

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:11434")
        };

        var projectRoot = Path.Combine(Path.GetTempPath(), $"aibox-route-test-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                    ["AiBox:LocalCoder:WorkspaceRoots:0"] = projectRoot
                })
                .Build();

            var environment = Substitute.For<IWebHostEnvironment>();
            environment.ContentRootPath.Returns(projectRoot);

            var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
            var contextService = Substitute.For<ILocalCoderContextService>();
            var service = CreateService(httpClient, configuration, environment, patchEditOperationService, contextService);

            var result = await service.CreatePlanAsync(
                new LocalCoderRequest
                {
                    ProjectPath = projectRoot,
                    Model = "legacy-model",
                    Task = "Create a plan"
                },
                null,
                new AgentModelRoute
                {
                    Id = "llamacpp-local-coder",
                    Name = "llama.cpp Local Coder",
                    Provider = "llama.cpp",
                    BaseUrl = "http://localhost:8082/v1/chat/completions",
                    Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf"
                });

            Assert.Equal("Plan", result.Plan);
            Assert.Single(requests);
            Assert.Equal("http://localhost:8082/v1/chat/completions", requests[0].ToString());
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<string> responses = new();
        private readonly List<string> requests;

        public QueueHandler(List<string> requests, params string[] responses)
        {
            this.requests = requests;
            foreach (var response in responses)
            {
                this.responses.Enqueue(response);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (responses.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("No more responses configured", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RouteCapturingHandler : HttpMessageHandler
    {
        private readonly Queue<string> responses = new();
        private readonly List<Uri> requests;

        public RouteCapturingHandler(List<Uri> requests, params string[] responses)
        {
            this.requests = requests;
            foreach (var response in responses)
            {
                this.responses.Enqueue(response);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!);
            if (responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("No more responses configured", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }
}
