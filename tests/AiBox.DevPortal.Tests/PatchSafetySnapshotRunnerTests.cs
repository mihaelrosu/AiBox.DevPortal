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

public sealed class PatchSafetySnapshotRunnerTests
{
    [Fact]
    public async Task ApplyPatchPreviewAsync_CreatesSafetySnapshotBeforeApply()
    {
        await using var context = CreateContext(InitializeGitRepo: true);

        var profile = new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            PolicyId = "ToolRunner"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-snapshot",
            ProjectPath = context.Root,
            RequiresApproval = true,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = "tester",
            PatchText = "diff --git a/Existing.cs b/Existing.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "Existing.cs",
                    OldContent = "before",
                    NewContent = "after"
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
                ChangedFiles = ["Existing.cs"]
            }));

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);

        var snapshot = Assert.Single(await context.SnapshotService.GetLatestAsync(10));
        Assert.True(snapshot.Success);
        Assert.Equal(preview.Id, snapshot.PatchPreviewId);
        Assert.True(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "Existing.cs")));

        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.Equal(snapshot.Id, audit.SnapshotId);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_MissingCreatedFile_DoesNotFailSnapshot()
    {
        await using var context = CreateContext(InitializeGitRepo: true);

        var profile = new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            PolicyId = "ToolRunner"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-missing",
            ProjectPath = context.Root,
            RequiresApproval = true,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = "tester",
            PatchText = "diff --git a/Created.cs b/Created.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "Created.cs",
                    OldContent = string.Empty,
                    NewContent = "new file"
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
                ChangedFiles = ["Created.cs"]
            }));

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);

        var snapshot = Assert.Single(await context.SnapshotService.GetLatestAsync(10));
        Assert.True(snapshot.Success);
        Assert.Contains("Missing target files recorded", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(snapshot.SnapshotDirectory, "Created.cs")));
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_SnapshotFailureBlocksApply()
    {
        await using var context = CreateContext(InitializeGitRepo: false);

        var profile = new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            PolicyId = "ToolRunner"
        };

        var preview = new LocalCoderPatchPreview
        {
            Id = "preview-failure",
            ProjectPath = context.Root,
            RequiresApproval = true,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = "tester",
            PatchText = "diff --git a/Existing.cs b/Existing.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "Existing.cs",
                    OldContent = "before",
                    NewContent = "after"
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.ApplyPatchPreviewAsync(preview, profile));

        Assert.StartsWith("Patch safety snapshot failed:", exception.Message, StringComparison.Ordinal);
        await context.CoderConsoleService.DidNotReceiveWithAnyArgs().ApplyPatchPreviewAsync(default!);

        var snapshot = Assert.Single(await context.SnapshotService.GetLatestAsync(10));
        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));

        Assert.Equal(snapshot.Id, audit.SnapshotId);
        Assert.False(audit.Success);
        Assert.StartsWith("Patch safety snapshot failed:", audit.Message, StringComparison.Ordinal);
    }

    private static RunnerContext CreateContext(bool InitializeGitRepo)
    {
        var root = Path.Combine(Path.GetTempPath(), $"patch-safety-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        if (InitializeGitRepo)
        {
            File.WriteAllText(Path.Combine(root, "Existing.cs"), "before");
            InitializeGitRepository(root);
        }

        var environment = new TestWebHostEnvironment(root);
        var routeService = new AgentModelRouteService(environment);
        var healthService = new AgentModelRouteHealthCheckService(new TestHttpClientFactory());
        var policyService = new AgentExecutionPolicyService(environment);
        var snapshotService = new PatchSafetySnapshotService(environment);
        var auditService = new PatchApplyAuditService(environment);
        var timelineService = new AgentRunTimelineService(environment);
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

        historyService.AddAsync(Arg.Any<AgentRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<AgentRunRecord>()));

        var runner = new AgentModeRunner(coderConsoleService, historyService, instructionService, routeService, healthService, policyService, snapshotService, auditService, timelineService);
        return new RunnerContext(root, runner, coderConsoleService, snapshotService, auditService);
    }

    private static void InitializeGitRepository(string root)
    {
        RunGitCommand(root, ["init"]);
        RunGitCommand(root, ["checkout", "-b", "main"]);
        RunGitCommand(root, ["config", "user.email", "test@example.com"]);
        RunGitCommand(root, ["config", "user.name", "Test User"]);
        RunGitCommand(root, ["add", "-A"]);
        RunGitCommand(root, ["commit", "-m", "Initial baseline"]);
    }

    private static void RunGitCommand(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git process.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: git {string.Join(" ", arguments)}{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }

    private sealed record RunnerContext(
        string Root,
        AgentModeRunner Runner,
        ICoderConsoleService CoderConsoleService,
        PatchSafetySnapshotService SnapshotService,
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
