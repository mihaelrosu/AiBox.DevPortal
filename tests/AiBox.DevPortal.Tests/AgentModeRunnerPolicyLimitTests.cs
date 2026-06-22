using System.Net;
using System.Net.Http;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModeRunnerPolicyLimitTests
{
    [Fact]
    public async Task GeneratePatchPreviewAsync_AllowsPatchWithinPolicyLimits()
    {
        await using var context = CreateContext();

        var profile = CreateProfile("PatchBuilder");
        var preview = CreatePreview(
            "Models/Allowed.cs",
            "diff --git a/Models/Allowed.cs b/Models/Allowed.cs\n--- a/Models/Allowed.cs\n+++ b/Models/Allowed.cs\n@@ -1 +1 @@\n-old\n+new");

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(preview));

        var result = await context.Runner.GeneratePatchPreviewAsync(CreateRequest("Update Models/Allowed.cs", "Models/Allowed.cs"), profile);

        Assert.Equal(preview.PatchText, result.PatchText);
        await context.CoderConsoleService.Received(1).GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>());
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_RejectsTooManyFiles()
    {
        await using var context = CreateContext();
        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "FileLimit",
            Name = "FileLimit",
            Description = "File limit policy",
            AllowModelCalls = true,
            AllowPatchGeneration = true,
            AllowPatchApply = false,
            AllowShellCommands = false,
            AllowToolCalls = false,
            MaxFilesChanged = 1,
            MaxPatchOperations = 0
        });

        var profile = CreateProfile("FileLimit");
        var preview = CreatePreview(
            "Models/One.cs",
            "diff --git a/Models/One.cs b/Models/One.cs\n--- a/Models/One.cs\n+++ b/Models/One.cs\n@@ -1 +1 @@\n-old\n+new",
            "Models/Two.cs",
            "diff --git a/Models/Two.cs b/Models/Two.cs\n--- a/Models/Two.cs\n+++ b/Models/Two.cs\n@@ -1 +1 @@\n-old\n+new");

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(preview));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.GeneratePatchPreviewAsync(CreateRequest("Update multiple files", "Models/One.cs", "Models/Two.cs"), profile));

        Assert.Equal("Patch changes too many files. Limit is 1.", exception.Message);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_RejectsTooManyOperations()
    {
        await using var context = CreateContext();
        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "OperationLimit",
            Name = "OperationLimit",
            Description = "Operation limit policy",
            AllowModelCalls = true,
            AllowPatchGeneration = true,
            AllowPatchApply = false,
            AllowShellCommands = false,
            AllowToolCalls = false,
            MaxFilesChanged = 0,
            MaxPatchOperations = 1
        });

        var profile = CreateProfile("OperationLimit");
        var preview = CreatePreview(
            "Models/One.cs",
            "diff --git a/Models/One.cs b/Models/One.cs\n--- a/Models/One.cs\n+++ b/Models/One.cs\n@@ -1 +1 @@\n-old\n+new",
            "Models/Two.cs",
            "diff --git a/Models/Two.cs b/Models/Two.cs\n--- a/Models/Two.cs\n+++ b/Models/Two.cs\n@@ -1 +1 @@\n-old\n+new");

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(preview));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.GeneratePatchPreviewAsync(CreateRequest("Update multiple files", "Models/One.cs", "Models/Two.cs"), profile));

        Assert.Equal("Patch has too many operations. Limit is 1.", exception.Message);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_RejectsBlockedDirectory()
    {
        await using var context = CreateContext();
        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "BlockedDirectory",
            Name = "BlockedDirectory",
            Description = "Blocked directory policy",
            AllowModelCalls = true,
            AllowPatchGeneration = true,
            AllowPatchApply = false,
            AllowShellCommands = false,
            AllowToolCalls = false,
            MaxFilesChanged = 0,
            MaxPatchOperations = 0,
            BlockedDirectories = ["Services/Secret/"]
        });

        var profile = CreateProfile("BlockedDirectory");
        var preview = CreatePreview(
            "Services/Secret/Hidden.cs",
            "diff --git a/Services/Secret/Hidden.cs b/Services/Secret/Hidden.cs\n--- a/Services/Secret/Hidden.cs\n+++ b/Services/Secret/Hidden.cs\n@@ -1 +1 @@\n-old\n+new");

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(preview));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.GeneratePatchPreviewAsync(CreateRequest("Update Services/Secret/Hidden.cs", "Services/Secret/Hidden.cs"), profile));

        Assert.Equal("Patch targets blocked directory: Services/Secret/", exception.Message);
    }

    [Fact]
    public async Task GeneratePatchPreviewAsync_RejectsDirectoryNotAllowed()
    {
        await using var context = CreateContext();
        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "AllowedDirectory",
            Name = "AllowedDirectory",
            Description = "Allowed directory policy",
            AllowModelCalls = true,
            AllowPatchGeneration = true,
            AllowPatchApply = false,
            AllowShellCommands = false,
            AllowToolCalls = false,
            MaxFilesChanged = 0,
            MaxPatchOperations = 0,
            AllowedDirectories = ["Components/"]
        });

        var profile = CreateProfile("AllowedDirectory");
        var preview = CreatePreview(
            "Models/Blocked.cs",
            "diff --git a/Models/Blocked.cs b/Models/Blocked.cs\n--- a/Models/Blocked.cs\n+++ b/Models/Blocked.cs\n@@ -1 +1 @@\n-old\n+new");

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>(), Arg.Any<AgentModelRoute?>())
            .Returns(Task.FromResult(preview));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Runner.GeneratePatchPreviewAsync(CreateRequest("Update Models/Blocked.cs", "Models/Blocked.cs"), profile));

        Assert.Equal("Patch targets directory not allowed by policy: Models/", exception.Message);
    }

    private static TestContextBundle CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-mode-policy-limits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Data"));

        var environment = new TestWebHostEnvironment(root);
        var routeService = new AgentModelRouteService(environment);
        var healthService = new AgentModelRouteHealthCheckService(new TestHttpClientFactory());
        var policyService = new AgentExecutionPolicyService(environment);
        var instructionService = new AgentInstructionService(environment);
        var coderConsoleService = Substitute.For<ICoderConsoleService>();
        var historyService = Substitute.For<IAgentRunHistoryService>();
        var snapshotService = new PatchSafetySnapshotService(environment);
        var auditService = new PatchApplyAuditService(environment);
        var timelineService = new AgentRunTimelineService(environment);

        historyService.AddAsync(Arg.Any<AgentRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<AgentRunRecord>()));

        var runner = new AgentModeRunner(coderConsoleService, historyService, instructionService, routeService, healthService, policyService, snapshotService, auditService, timelineService);
        return new TestContextBundle(root, runner, policyService, coderConsoleService, auditService);
    }

    private static async Task UpsertPolicyAsync(AgentExecutionPolicyService policyService, AgentExecutionPolicy policy)
    {
        var policies = (await policyService.GetAllAsync()).ToList();
        var index = policies.FindIndex(item => item.Id.Equals(policy.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            policies[index] = policy;
        }
        else
        {
            policies.Add(policy);
        }

        await policyService.SaveAsync(policies);
    }

    private static LocalCoderRequest CreateRequest(string task, params string[] filePaths)
    {
        return new LocalCoderRequest
        {
            ProjectPath = "/project",
            Model = "legacy-model",
            Task = task,
            FileContexts = filePaths.Select(path => new LocalCoderFileContext
            {
                RelativePath = path,
                Content = "placeholder"
            }).ToList()
        };
    }

    private static AgentModeProfile CreateProfile(string policyId)
    {
        return new AgentModeProfile
        {
            Id = $"profile-{policyId}",
            Mode = AgentMode.PatchBuilder,
            Name = $"Profile {policyId}",
            Model = "legacy-model",
            ModelRouteId = "llamacpp-local-coder",
            PolicyId = policyId
        };
    }

    private static LocalCoderPatchPreview CreatePreview(params string[] filePathAndPatchTextPairs)
    {
        var changes = new List<PatchFileChange>();
        for (var index = 0; index < filePathAndPatchTextPairs.Length; index += 2)
        {
            changes.Add(new PatchFileChange
            {
                RelativePath = filePathAndPatchTextPairs[index],
                OldContent = "old",
                NewContent = "new"
            });
        }

        return new LocalCoderPatchPreview
        {
            ProjectPath = "/project",
            Model = "legacy-model",
            Task = "test",
            FileChanges = changes,
            PatchText = string.Join(Environment.NewLine, filePathAndPatchTextPairs.Where((_, index) => index % 2 == 1))
        };
    }

    private sealed record TestContextBundle(
        string Root,
        AgentModeRunner Runner,
        AgentExecutionPolicyService PolicyService,
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

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHandler());
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
