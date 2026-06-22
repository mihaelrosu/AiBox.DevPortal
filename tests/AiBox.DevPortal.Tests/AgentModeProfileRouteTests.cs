using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using NSubstitute;
using Xunit;
using ConsoleLocalCoderTask = AiBox.DevPortal.Models.LocalCoderTask;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModeProfileRouteTests
{
    [Fact]
    public async Task CreatePlanAsync_ResolvesPlannerRoute()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.Planner);
        Assert.NotNull(profile);
        Assert.Equal("llamacpp-local-coder", profile!.ModelRouteId);
        Assert.Equal("Planner", profile.PolicyId);

        LocalCoderRequest? routedRequest = null;
        context.CoderConsoleService
            .CreatePlanAsync(
                Arg.Do<LocalCoderRequest>(request => routedRequest = request),
                Arg.Any<AgentModeProfile>(),
                Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(new ConsoleLocalCoderTask
            {
                ProjectPath = context.Root,
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                Task = "Create planner route test",
                Plan = "plan"
            }));

        var result = await context.Runner.CreatePlanAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Create planner route test",
            Model = "legacy-model"
        }, profile);

        Assert.NotNull(routedRequest);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", routedRequest!.Model);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", result.Model);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_ResolvesPatchBuilderRoute()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(profile);
        Assert.Equal("llamacpp-local-coder", profile!.ModelRouteId);
        Assert.Equal("PatchBuilder", profile.PolicyId);

        LocalCoderRequest? routedRequest = null;
        context.CoderConsoleService
            .GeneratePatchPreviewAsync(
                Arg.Do<LocalCoderRequest>(request => routedRequest = request),
                Arg.Any<AgentModeProfile>(),
                Arg.Any<PatchPreviewRepairContext?>(),
                Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(new LocalCoderPatchPreview
            {
                ProjectPath = context.Root,
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                Task = "Update Models/Existing.cs",
                PatchText = "diff --git a/Models/Existing.cs b/Models/Existing.cs"
            }));

        var result = await context.Runner.GeneratePatchPreviewAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Update Models/Existing.cs",
            Model = "legacy-model",
            FileContexts =
            [
                new LocalCoderFileContext
                {
                    RelativePath = "Models/Existing.cs",
                    FullPath = Path.Combine(context.Root, "Models", "Existing.cs"),
                    Content = "namespace Demo;"
                }
            ]
        }, profile);

        Assert.NotNull(routedRequest);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", routedRequest!.Model);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", result.Model);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_AllowsCreateOnlyTaskWithoutSelectedFiles()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(profile);

        LocalCoderRequest? routedRequest = null;
        context.CoderConsoleService
            .GeneratePatchPreviewAsync(
                Arg.Do<LocalCoderRequest>(request => routedRequest = request),
                Arg.Any<AgentModeProfile>(),
                Arg.Any<PatchPreviewRepairContext?>(),
                Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(new LocalCoderPatchPreview
            {
                ProjectPath = context.Root,
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                Task = "Create Models/TestFeature.cs",
                PatchText = "diff --git a/Models/TestFeature.cs b/Models/TestFeature.cs"
            }));

        var result = await context.Runner.GeneratePatchPreviewAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Create Models/TestFeature.cs",
            Model = "legacy-model"
        }, profile);

        Assert.NotNull(routedRequest);
        Assert.Empty(routedRequest!.FileContexts);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", routedRequest.Model);
        Assert.Equal("qwen2.5-coder-7b-instruct-q4_k_m.gguf", result.Model);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_PolicyRequiresApproval_MarksPreview()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(profile);

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(
                Arg.Any<LocalCoderRequest>(),
                Arg.Any<AgentModeProfile>(),
                Arg.Any<PatchPreviewRepairContext?>(),
                Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(new LocalCoderPatchPreview
            {
                ProjectPath = context.Root,
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                Task = "Update Models/Existing.cs",
                PatchText = "diff --git a/Models/Existing.cs b/Models/Existing.cs"
            }));

        var preview = await context.Runner.GeneratePatchPreviewAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Update Models/Existing.cs",
            Model = "legacy-model",
            FileContexts =
            [
                new LocalCoderFileContext
                {
                    RelativePath = "Models/Existing.cs",
                    FullPath = Path.Combine(context.Root, "Models", "Existing.cs"),
                    Content = "namespace Demo;"
                }
            ]
        }, profile);

        Assert.True(preview.RequiresApproval);
        Assert.Equal("Execution policy requires human approval before apply.", preview.ApprovalReason);
        Assert.Null(preview.ApprovedAt);
        Assert.Equal(string.Empty, preview.ApprovedBy);
    }

    [Fact]
    public async Task CreatePlanAsync_MissingRouteFailsClearly()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = new AgentModeProfile
        {
            Id = "custom-planner",
            Mode = AgentMode.Planner,
            Name = "Custom Planner",
            Model = "legacy-model",
            ModelRouteId = string.Empty,
            PolicyId = "Planner"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.CreatePlanAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Create planner route test"
        }, profile));

        Assert.Equal("Agent profile 'Custom Planner' does not have a valid model route.", exception.Message);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_BlockedBeforeApproval()
    {
        await using var context = CreateContext(healthyRoute: true);

        var applyProfile = await context.ProfileService.GetByModeAsync(AgentMode.ToolRunner);
        Assert.NotNull(applyProfile);

        var preview = new LocalCoderPatchPreview
        {
            ProjectPath = context.Root,
            Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
            Task = "Update Models/Existing.cs",
            PatchText = "diff --git a/Models/Existing.cs b/Models/Existing.cs",
            RequiresApproval = true,
            ApprovalReason = "Execution policy requires human approval before apply."
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.ApplyPatchPreviewAsync(preview, applyProfile!));

        Assert.Equal("Patch preview requires human approval before apply.", exception.Message);
    }

    [Fact]
    public async Task ApprovePatchPreviewAsync_EnablesApply()
    {
        await using var context = CreateContext(healthyRoute: true);

        var previewProfile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(previewProfile);
        var applyProfile = await context.ProfileService.GetByModeAsync(AgentMode.ToolRunner);
        Assert.NotNull(applyProfile);

        var preview = new LocalCoderPatchPreview
        {
            ProjectPath = context.Root,
            Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
            Task = "Update Models/Existing.cs",
            PatchText = "diff --git a/Models/Existing.cs b/Models/Existing.cs",
            RequiresApproval = true,
            ApprovalReason = "Execution policy requires human approval before apply."
        };

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
            Message = "Applied"
            }));

        var approved = await context.Runner.ApprovePatchPreviewAsync(preview, previewProfile!, "tester");
        Assert.True(approved.RequiresApproval);
        Assert.NotNull(approved.ApprovedAt);
        Assert.Equal("tester", approved.ApprovedBy);

        var result = await context.Runner.ApplyPatchPreviewAsync(approved, applyProfile!);

        Assert.True(result.Applied);
        await context.CoderConsoleService.Received(1).ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>());
    }

    [Fact]
    public async Task ApprovePatchPreviewAsync_DoesNotApplyPatch()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(profile);

        var preview = new LocalCoderPatchPreview
        {
            ProjectPath = context.Root,
            Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
            Task = "Update Models/Existing.cs",
            PatchText = "diff --git a/Models/Existing.cs b/Models/Existing.cs",
            RequiresApproval = true,
            ApprovalReason = "Execution policy requires human approval before apply."
        };

        await context.Runner.ApprovePatchPreviewAsync(preview, profile!, "tester");

        await context.CoderConsoleService.DidNotReceiveWithAnyArgs().ApplyPatchPreviewAsync(default!);
    }

    [Fact]
    public async Task VerifyProjectAsync_AllowsVerifierWithoutModelRoute()
    {
        await using var context = CreateContext(healthyRoute: true);

        context.CoderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<CommandRunResult>>([]));

        var profile = new AgentModeProfile
        {
            Id = "verifier",
            Mode = AgentMode.Verifier,
            Name = "Verifier",
            Model = string.Empty,
            ModelRouteId = string.Empty,
            PolicyId = "Verifier"
        };

        var results = await context.Runner.VerifyProjectAsync(context.Root, profile, []);

        Assert.Empty(results);
        await context.CoderConsoleService.Received(1).VerifyProjectAsync(
            context.Root,
            Arg.Is<IReadOnlyList<string>?>(commands => commands != null && commands.Count == 0));
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_PolicyBlocksPatchGeneration()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = new AgentModeProfile
        {
            Id = "custom-patch-builder",
            Mode = AgentMode.PatchBuilder,
            Name = "Custom Patch Builder",
            Model = "legacy-model",
            ModelRouteId = "llamacpp-local-coder",
            PolicyId = "Planner"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.GeneratePatchPreviewAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Update Models/Existing.cs",
            Model = "legacy-model"
        }, profile));

        Assert.Equal("Agent profile 'Custom Patch Builder' does not allow patch generation required for Generate Patch Preview.", exception.Message);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_PolicyBlocksPatchApply()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = new AgentModeProfile
        {
            Id = "custom-tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Custom Tool Runner",
            Model = "legacy-model",
            PolicyId = "Planner"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.ApplyPatchPreviewAsync(new LocalCoderPatchPreview
        {
            ProjectPath = context.Root,
            Task = "Apply patch",
            PatchText = "diff --git a/Example.cs b/Example.cs"
        }, profile));

        Assert.Equal("Agent profile 'Custom Tool Runner' does not allow patch apply required for Apply Patch.", exception.Message);
    }

    [Fact]
    public async Task RunCommandAsync_PolicyBlocksShellCommands()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = new AgentModeProfile
        {
            Id = "custom-tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Custom Tool Runner",
            Model = "legacy-model",
            PolicyId = "Planner"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.RunCommandAsync(context.Root, "dotnet build", profile));

        Assert.Equal("Agent profile 'Custom Tool Runner' does not allow shell commands required for Run Command.", exception.Message);
    }

    [Fact]
    public async Task RunCommandAsync_PolicyBlocksToolCalls()
    {
        await using var context = CreateContext(healthyRoute: true);

        var profile = new AgentModeProfile
        {
            Id = "custom-tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Custom Tool Runner",
            Model = "legacy-model",
            PolicyId = "Verifier"
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.RunCommandAsync(context.Root, "dotnet build", profile));

        Assert.Equal("Agent profile 'Custom Tool Runner' does not allow tool calls required for Run Command.", exception.Message);
    }

    [Fact]
    public async Task CreatePlanAsync_UnhealthyRouteStopsRun()
    {
        await using var context = CreateContext(healthyRoute: false);

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.Planner);
        Assert.NotNull(profile);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.CreatePlanAsync(new LocalCoderRequest
        {
            ProjectPath = context.Root,
            Task = "Create planner route test"
        }, profile!));

        Assert.Equal("Model route 'llama.cpp / DevPortal Local Coder' is not reachable at http://localhost:8082/v1/chat/completions.", exception.Message);
        await context.CoderConsoleService.DidNotReceiveWithAnyArgs().CreatePlanAsync(default!, default!);
    }

    private static TestContextBundle CreateContext(bool healthyRoute)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-mode-profile-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        InitializeGitRepository(root);

        var environment = new TestWebHostEnvironment(root);
        var routeService = new AgentModelRouteService(environment);
        var healthCheckService = new AgentModelRouteHealthCheckService(new TestHttpClientFactory(healthyRoute));
        var policyService = new AgentExecutionPolicyService(environment);
        var profileService = new AgentModeProfileService(environment);
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

        var runner = new AgentModeRunner(coderConsoleService, historyService, instructionService, routeService, healthCheckService, policyService, snapshotService, auditService, timelineService);
        return new TestContextBundle(root, runner, profileService, coderConsoleService, auditService);
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

    private sealed record TestContextBundle(
        string Root,
        AgentModeRunner Runner,
        AgentModeProfileService ProfileService,
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
        public string ApplicationName { get; set; } = "AiBox.DevPortal.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class TestHttpClientFactory(bool healthy) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHandler(healthy));
    }

    private sealed class TestHandler(bool healthy) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        }
    }
}
