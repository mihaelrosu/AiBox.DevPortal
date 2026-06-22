using System.Diagnostics;
using System.Net;
using System.Net.Http;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchApplyAuditServiceTests
{
    [Fact]
    public async Task AddAsync_AppendsWithoutDeletingPreviousItems()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            await service.AddAsync(new PatchApplyAuditItem
            {
                Id = "item-1",
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1),
                Action = "Approved",
                PatchPreviewId = "preview-1",
                ProfileId = "profile-1",
                PolicyId = "policy-1",
                ModelRouteId = "route-1",
                Success = true,
                Message = "first",
                ChangedFiles = ["A.cs"]
            });

            await service.AddAsync(new PatchApplyAuditItem
            {
                Id = "item-2",
                Timestamp = DateTimeOffset.UtcNow,
                Action = "ApplySucceeded",
                PatchPreviewId = "preview-2",
                ProfileId = "profile-2",
                PolicyId = "policy-2",
                ModelRouteId = "route-2",
                Success = true,
                Message = "second",
                ChangedFiles = ["B.cs"]
            });

            var latest = await service.GetLatestAsync(10);

            Assert.Equal(2, latest.Count);
            Assert.Equal("preview-2", latest[0].PatchPreviewId);
            Assert.Equal("preview-1", latest[1].PatchPreviewId);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task Approval_WritesAuditItem()
    {
        await using var context = CreateRunnerContext();

        var profile = new AgentModeProfile
        {
            Id = "patch-builder",
            Mode = AgentMode.PatchBuilder,
            Name = "Patch Builder",
            Model = "legacy-model",
            ModelRouteId = "llamacpp-local-coder",
            PolicyId = "PatchBuilder"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-approval",
            ProjectPath = context.Root,
            Task = "Update file",
            PatchText = "diff --git a/A.cs b/A.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "A.cs",
                    OldContent = "old",
                    NewContent = "new"
                }
            ]
        };

        await context.Runner.ApprovePatchPreviewAsync(preview, profile, "tester");

        var audit = await context.AuditService.GetLatestAsync(10);

        Assert.Single(audit);
        Assert.Equal("Approved", audit[0].Action);
        Assert.True(audit[0].Success);
        Assert.Equal("preview-approval", audit[0].PatchPreviewId);
        Assert.Equal("tester", audit[0].ApprovedBy);
        Assert.Single(audit[0].ChangedFiles);
    }

    [Fact]
    public async Task BlockedApply_WritesAuditItem()
    {
        await using var context = CreateRunnerContext();

        var profile = new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            ModelRouteId = string.Empty,
            PolicyId = "Planner"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-blocked",
            ProjectPath = context.Root,
            Task = "Apply patch",
            PatchText = "diff --git a/A.cs b/A.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "A.cs",
                    OldContent = "old",
                    NewContent = "new"
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.ApplyPatchPreviewAsync(preview, profile));

        Assert.Equal("Agent profile 'Tool Runner' does not allow patch apply required for Apply Patch.", exception.Message);

        var audit = await context.AuditService.GetLatestAsync(10);

        Assert.Single(audit);
        Assert.Equal("ApplyBlocked", audit[0].Action);
        Assert.False(audit[0].Success);
        Assert.Equal("preview-blocked", audit[0].PatchPreviewId);
    }

    [Fact]
    public async Task SuccessfulApply_WritesAuditItem()
    {
        await using var context = CreateRunnerContext();

        var profile = new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            ModelRouteId = string.Empty,
            PolicyId = "ToolRunner"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-success",
            ProjectPath = context.Root,
            Task = "Apply patch",
            PatchText = "diff --git a/A.cs b/A.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "A.cs",
                    OldContent = "old",
                    NewContent = "new"
                }
            ]
        };

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
                Message = "Applied",
                ChangedFiles = ["A.cs"]
            }));

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);

        var audit = await context.AuditService.GetLatestAsync(10);

        Assert.Single(audit);
        Assert.Equal("ApplySucceeded", audit[0].Action);
        Assert.True(audit[0].Success);
        Assert.Equal("preview-success", audit[0].PatchPreviewId);
        Assert.Contains("A.cs", audit[0].ChangedFiles);
        Assert.True(audit[0].VerificationSucceeded);
        Assert.NotEmpty(audit[0].VerificationSummary);
        Assert.False(audit[0].RestoreAttempted);
        Assert.False(audit[0].RestoreSucceeded);
    }

    private static PatchApplyAuditService CreateService(string root)
    {
        return new PatchApplyAuditService(new TestWebHostEnvironment(root));
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"patch-apply-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RunnerContext CreateRunnerContext()
    {
        var root = CreateTempProjectRoot();
        InitializeGitRepository(root);
        var environment = new TestWebHostEnvironment(root);
        var routeService = new AgentModelRouteService(environment);
        var healthService = new AgentModelRouteHealthCheckService(new TestHttpClientFactory());
        var policyService = new AgentExecutionPolicyService(environment);
        var instructionService = new AgentInstructionService(environment);
        var coderConsoleService = Substitute.For<ICoderConsoleService>();
        coderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<CommandRunResult>>([
                new CommandRunResult
                {
                    Command = "dotnet build",
                    ExitCode = 0,
                    Output = string.Empty,
                    Error = string.Empty
                }
            ]));
        var historyService = Substitute.For<IAgentRunHistoryService>();
        var snapshotService = new PatchSafetySnapshotService(environment);
        var auditService = new PatchApplyAuditService(environment);
        var timelineService = new AgentRunTimelineService(environment);

        historyService.AddAsync(Arg.Any<AgentRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<AgentRunRecord>()));

        var runner = new AgentModeRunner(coderConsoleService, historyService, instructionService, routeService, healthService, policyService, snapshotService, auditService, timelineService);
        return new RunnerContext(root, runner, coderConsoleService, auditService);
    }

    private static void InitializeGitRepository(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("init");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git process.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not initialize git repository.");
        }
    }

    private sealed record RunnerContext(
        string Root,
        AgentModeRunner Runner,
        ICoderConsoleService CoderConsoleService,
        PatchApplyAuditService AuditService) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHandler());
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
