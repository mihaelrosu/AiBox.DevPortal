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
            var metrics = service.GetPatchPreviewMetrics();
            Assert.Equal(1, metrics.Attempts);
            Assert.Equal(1, metrics.SuccessfulPreviews);
            Assert.Equal(0, metrics.FailedPreviews);
            Assert.Equal(1, metrics.RepairedPreviews);
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
            var metrics = service.GetPatchPreviewMetrics();
            Assert.Equal(1, metrics.Attempts);
            Assert.Equal(1, metrics.SuccessfulPreviews);
            Assert.Equal(0, metrics.FailedPreviews);
            Assert.Equal(1, metrics.RepairedPreviews);
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
            var metrics = service.GetPatchPreviewMetrics();
            Assert.Equal(1, metrics.Attempts);
            Assert.Equal(1, metrics.SuccessfulPreviews);
            Assert.Equal(0, metrics.FailedPreviews);
            Assert.Equal(1, metrics.RepairedPreviews);
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

            var metrics = service.GetPatchPreviewMetrics();
            Assert.Equal(1, metrics.Attempts);
            Assert.Equal(0, metrics.SuccessfulPreviews);
            Assert.Equal(1, metrics.FailedPreviews);
            Assert.Equal(0, metrics.RepairedPreviews);
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
