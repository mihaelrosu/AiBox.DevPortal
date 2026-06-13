using System.Net;
using System.Net.Http;
using System.Text;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class CoderConsoleServiceRepairTests
{
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
                  "response": "{\"operations\":[{\"filePath\":\"Example.cs\",\"operation\":\"insert_before\",\"anchor\":\"public class Example {}\",\"oldText\":\"\",\"newText\":\"// note\\n\"}]}",
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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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
            Assert.Equal(0, requests.Count);
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

            var service = new CoderConsoleService(
                httpClient,
                configuration,
                environment,
                patchEditOperationService,
                contextService);

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
}
