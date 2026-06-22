using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

public sealed class AgentRunTimelineTests
{
    [Fact]
    public async Task SuccessfulApply_RecordsTimelineInExpectedOrder()
    {
        await using var context = CreateContext();

        var patchBuilderProfile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        var toolRunnerProfile = await context.ProfileService.GetByModeAsync(AgentMode.ToolRunner);
        Assert.NotNull(patchBuilderProfile);
        Assert.NotNull(toolRunnerProfile);

        context.CoderConsoleService
            .GeneratePatchPreviewAsync(Arg.Any<LocalCoderRequest>(), Arg.Any<AgentModeProfile>(), Arg.Any<PatchPreviewRepairContext?>())
            .Returns(Task.FromResult(new LocalCoderPatchPreview
            {
                ProjectPath = context.Root,
                Task = "Update A.cs",
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                PatchText = "diff --git a/A.cs b/A.cs",
                FileChanges =
                [
                    new PatchFileChange
                    {
                        RelativePath = "A.cs",
                        OldContent = "before",
                        NewContent = "after"
                    }
                ],
                VerificationCommands = ["dotnet test"],
                RequiresApproval = true,
                ApprovalReason = "Execution policy requires human approval before apply."
            }));

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
                Message = "Applied",
                ChangedFiles = ["A.cs"]
            }));

        context.CoderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<CommandRunResult>>([
                new CommandRunResult
                {
                    Command = "dotnet test",
                    ExitCode = 0,
                    Output = "verification ok",
                    Error = string.Empty
                }
            ]));

        var preview = await context.Runner.GeneratePatchPreviewAsync(CreateRequest(context.Root), patchBuilderProfile!);
        var approved = await context.Runner.ApprovePatchPreviewAsync(preview, patchBuilderProfile!, "tester");
        var result = await context.Runner.ApplyPatchPreviewAsync(approved, toolRunnerProfile!);

        Assert.True(result.Applied);
        Assert.True(result.VerificationSucceeded);
        Assert.False(result.RestoreAttempted);

        var items = await context.TimelineService.GetByRunIdAsync(preview.Id);

        Assert.Equal(
            [
                "RouteHealthCheck",
                "PolicyValidation",
                "PatchPreviewGenerated",
                "ApprovalRequired",
                "PreviewApproved",
                "SnapshotCreated",
                "PatchApplyStarted",
                "PatchApplySucceeded",
                "VerificationSucceeded",
                "AuditWritten"
            ],
            items.Select(item => item.Stage).ToArray());
    }

    [Fact]
    public async Task VerificationFailure_RecordsVerificationFailed()
    {
        await using var context = CreateContext();

        var toolRunnerProfile = await context.ProfileService.GetByModeAsync(AgentMode.ToolRunner);
        Assert.NotNull(toolRunnerProfile);

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
                Message = "Applied",
                ChangedFiles = ["A.cs"]
            }));

        context.CoderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<CommandRunResult>>([
                new CommandRunResult
                {
                    Command = "dotnet build",
                    ExitCode = 1,
                    Output = string.Empty,
                    Error = "build failed"
                }
            ]));

        var preview = CreatePreview(context.Root, "timeline-verification-failed");
        var result = await context.Runner.ApplyPatchPreviewAsync(preview, toolRunnerProfile!);

        Assert.True(result.Applied);
        Assert.False(result.VerificationSucceeded);

        var items = await context.TimelineService.GetByRunIdAsync(preview.Id);

        Assert.Contains(items, item => item.Stage == "VerificationFailed");
        Assert.DoesNotContain(items, item => item.Stage == "RestoreAttempted");
    }

    [Fact]
    public async Task AutoRestore_RecordsRestoreAttemptAndSuccess()
    {
        await using var context = CreateContext();

        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "AutoRestoreToolRunner",
            Name = "Auto Restore Tool Runner",
            Description = "Tool runner policy with auto restore enabled.",
            AllowModelCalls = false,
            AllowToolCalls = true,
            AllowShellCommands = true,
            AllowPatchGeneration = false,
            AllowPatchApply = true,
            RequireHumanApproval = false,
            AutoRestoreOnVerificationFailure = true,
            MaxFilesChanged = 0,
            MaxPatchOperations = 0,
            MaxExecutionMinutes = 30
        });

        var profile = CreateToolRunnerProfile("AutoRestoreToolRunner");

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
                Message = "Applied",
                ChangedFiles = ["A.cs"]
            }));

        context.CoderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(callInfo =>
            {
                WriteFile(context.Root, "A.cs", "after");
                return Task.FromResult<IReadOnlyList<CommandRunResult>>([
                    new CommandRunResult
                    {
                        Command = "dotnet build",
                        ExitCode = 1,
                        Output = string.Empty,
                        Error = "build failed"
                    }
                ]);
            });

        var result = await context.Runner.ApplyPatchPreviewAsync(CreatePreview(context.Root, "timeline-auto-restore"), profile);

        Assert.True(result.RestoreAttempted);
        Assert.True(result.RestoreSucceeded);

        var items = await context.TimelineService.GetByRunIdAsync(result.Id);

        Assert.Contains(items, item => item.Stage == "RestoreAttempted");
        Assert.Contains(items, item => item.Stage == "RestoreSucceeded");
    }

    [Fact]
    public async Task AutoRestoreFailure_RecordsRestoreFailed()
    {
        await using var context = CreateContext();

        await UpsertPolicyAsync(context.PolicyService, new AgentExecutionPolicy
        {
            Id = "AutoRestoreToolRunner",
            Name = "Auto Restore Tool Runner",
            Description = "Tool runner policy with auto restore enabled.",
            AllowModelCalls = false,
            AllowToolCalls = true,
            AllowShellCommands = true,
            AllowPatchGeneration = false,
            AllowPatchApply = true,
            RequireHumanApproval = false,
            AutoRestoreOnVerificationFailure = true,
            MaxFilesChanged = 0,
            MaxPatchOperations = 0,
            MaxExecutionMinutes = 30
        });

        var profile = CreateToolRunnerProfile("AutoRestoreToolRunner");

        context.CoderConsoleService
            .ApplyPatchPreviewAsync(Arg.Any<LocalCoderPatchPreview>())
            .Returns(Task.FromResult(new LocalCoderPatchApplyResult
            {
                Applied = true,
                VerificationPassed = true,
                Message = "Applied",
                ChangedFiles = ["A.cs"]
            }));

        context.CoderConsoleService
            .VerifyProjectAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(callInfo =>
            {
                WriteFile(context.Root, "A.cs", "after");
                DeleteLatestSnapshotDirectory(context.Root);
                return Task.FromResult<IReadOnlyList<CommandRunResult>>([
                    new CommandRunResult
                    {
                        Command = "dotnet build",
                        ExitCode = 1,
                        Output = string.Empty,
                        Error = "build failed"
                    }
                ]);
            });

        var result = await context.Runner.ApplyPatchPreviewAsync(CreatePreview(context.Root, "timeline-auto-restore-failed"), profile);

        Assert.True(result.RestoreAttempted);
        Assert.False(result.RestoreSucceeded);
        Assert.NotEmpty(result.RestoreMessage);

        var items = await context.TimelineService.GetByRunIdAsync(result.Id);

        Assert.Contains(items, item => item.Stage == "RestoreAttempted");
        Assert.Contains(items, item => item.Stage == "RestoreFailed");
    }

    [Fact]
    public async Task ApprovalWritesPreviewApproved()
    {
        await using var context = CreateContext();

        var profile = await context.ProfileService.GetByModeAsync(AgentMode.PatchBuilder);
        Assert.NotNull(profile);

        var preview = CreatePreview(context.Root, "preview-approved");
        await context.Runner.ApprovePatchPreviewAsync(preview, profile!, "tester");

        var items = await context.TimelineService.GetByRunIdAsync(preview.Id);

        var item = Assert.Single(items);
        Assert.Equal("PreviewApproved", item.Stage);
        Assert.Equal("Succeeded", item.Status);
        Assert.Equal("preview-approved", item.ReferenceId);
    }

    [Fact]
    public async Task CorruptTimelineFile_IsRepairedOnReadAndWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-run-timeline-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var dataPath = Path.Combine(root, "Data", "agent-run-timeline.json");
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            await File.WriteAllTextAsync(dataPath, "{not valid json");

            var service = new AgentRunTimelineService(new TestWebHostEnvironment(root));

            var latest = await service.GetLatestAsync();
            Assert.Empty(latest);

            await service.AddAsync(new AgentRunTimelineItem
            {
                RunId = "run-1",
                Stage = "AuditWritten",
                Status = "Succeeded",
                Message = "written",
                ReferenceId = "ref-1"
            });

            var recovered = await service.GetLatestAsync();
            Assert.Single(recovered);
            Assert.Equal("run-1", recovered[0].RunId);
            Assert.Contains("\"run-1\"", await File.ReadAllTextAsync(dataPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static RunnerContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-run-timeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.cs"), "before");
        InitializeGitRepository(root);

        var environment = new TestWebHostEnvironment(root);
        var routeService = new AgentModelRouteService(environment);
        var healthService = new AgentModelRouteHealthCheckService(new TestHttpClientFactory(true));
        var policyService = new AgentExecutionPolicyService(environment);
        var profileService = new AgentModeProfileService(environment);
        var instructionService = new AgentInstructionService(environment);
        var coderConsoleService = Substitute.For<ICoderConsoleService>();
        var historyService = Substitute.For<IAgentRunHistoryService>();
        var snapshotService = new PatchSafetySnapshotService(environment);
        var auditService = new PatchApplyAuditService(environment);
        var timelineService = new AgentRunTimelineService(environment);

        historyService.AddAsync(Arg.Any<AgentRunRecord>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<AgentRunRecord>()));

        var runner = new AgentModeRunner(coderConsoleService, historyService, instructionService, routeService, healthService, policyService, snapshotService, auditService, timelineService);
        return new RunnerContext(root, runner, coderConsoleService, policyService, profileService, timelineService);
    }

    private static LocalCoderRequest CreateRequest(string root)
    {
        return new LocalCoderRequest
        {
            ProjectPath = root,
            Task = "Update A.cs",
            Model = "legacy-model",
            FileContexts =
            [
                new LocalCoderFileContext
                {
                    RelativePath = "A.cs",
                    FullPath = Path.Combine(root, "A.cs"),
                    Content = "before"
                }
            ]
        };
    }

    private static LocalCoderPatchPreview CreatePreview(string root, string id)
    {
        return new LocalCoderPatchPreview
        {
            Id = id,
            ProjectPath = root,
            Task = "Update A.cs",
            PatchText = "diff --git a/A.cs b/A.cs",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = "A.cs",
                    OldContent = "before",
                    NewContent = "after"
                }
            ],
            VerificationCommands = ["dotnet build"]
        };
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

    private static void WriteFile(string root, string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(root, relativePath), content);
    }

    private static void DeleteLatestSnapshotDirectory(string root)
    {
        var path = Path.Combine(root, "Data", "patch-safety-snapshots.json");
        if (!File.Exists(path))
        {
            return;
        }

        var snapshots = JsonSerializer.Deserialize<List<PatchSafetySnapshot>>(File.ReadAllText(path)) ?? [];
        var latestSnapshot = snapshots.LastOrDefault();
        if (latestSnapshot is null || string.IsNullOrWhiteSpace(latestSnapshot.SnapshotDirectory))
        {
            return;
        }

        if (Directory.Exists(latestSnapshot.SnapshotDirectory))
        {
            Directory.Delete(latestSnapshot.SnapshotDirectory, recursive: true);
        }
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
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git command failed: git {string.Join(" ", arguments)}");
        }
    }

    private sealed record RunnerContext(
        string Root,
        AgentModeRunner Runner,
        ICoderConsoleService CoderConsoleService,
        AgentExecutionPolicyService PolicyService,
        AgentModeProfileService ProfileService,
        AgentRunTimelineService TimelineService) : IAsyncDisposable
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
