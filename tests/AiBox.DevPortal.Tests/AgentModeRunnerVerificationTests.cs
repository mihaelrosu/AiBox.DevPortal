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
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModeRunnerVerificationTests
{
    [Fact]
    public async Task ApplyPatchPreviewAsync_RunsVerificationAndRecordsSuccess()
    {
        await using var context = CreateContext();

        var profile = CreateToolRunnerProfile();
        var preview = CreatePreview(context.Root, "A.cs", verificationCommands: ["dotnet test"]);

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

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);
        Assert.True(result.VerificationSucceeded);
        Assert.False(result.RestoreAttempted);
        Assert.False(result.RestoreSucceeded);
        Assert.Equal("dotnet test", result.VerificationRun.Command);
        Assert.Contains("verification ok", result.VerificationOutput, StringComparison.OrdinalIgnoreCase);

        await context.CoderConsoleService.Received(1).VerifyProjectAsync(
            preview.ProjectPath,
            Arg.Is<IReadOnlyList<string>?>(commands => commands is not null && commands.SequenceEqual(["dotnet test"])));

        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.True(audit.Success);
        Assert.True(audit.VerificationSucceeded);
        Assert.False(audit.RestoreAttempted);
        Assert.False(audit.RestoreSucceeded);
        Assert.Contains("Verification succeeded: True", audit.VerificationSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_VerificationFailureDoesNotRestoreWhenPolicyDisablesIt()
    {
        await using var context = CreateContext();

        var profile = CreateToolRunnerProfile();
        var preview = CreatePreview(context.Root, "A.cs");

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

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);
        Assert.False(result.VerificationSucceeded);
        Assert.False(result.VerificationPassed);
        Assert.False(result.RestoreAttempted);
        Assert.False(result.RestoreSucceeded);
        Assert.Equal(string.Empty, result.RestoreMessage);
        Assert.Equal("Patch applied, but verification failed.", result.Message);
        Assert.Equal("dotnet build", result.VerificationRun.Command);
        Assert.Equal(1, result.VerificationRun.ExitCode);
        Assert.Contains("build failed", result.VerificationOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("after", File.ReadAllText(Path.Combine(context.Root, "A.cs")));

        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.True(audit.Success);
        Assert.False(audit.VerificationSucceeded);
        Assert.False(audit.RestoreAttempted);
        Assert.False(audit.RestoreSucceeded);
        Assert.Contains("Verification succeeded: False", audit.VerificationSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore attempted: False", audit.RestoreSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_VerificationFailureRestoresSnapshotWhenPolicyEnablesIt()
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
        var preview = CreatePreview(context.Root, "A.cs");

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

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);
        Assert.False(result.VerificationSucceeded);
        Assert.False(result.VerificationPassed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.RestoreSucceeded);
        Assert.NotEmpty(result.RestoreMessage);
        Assert.Contains("restored", result.RestoreMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("before", File.ReadAllText(Path.Combine(context.Root, "A.cs")));

        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.True(audit.Success);
        Assert.False(audit.VerificationSucceeded);
        Assert.True(audit.RestoreAttempted);
        Assert.True(audit.RestoreSucceeded);
        Assert.Contains("Restore attempted: True", audit.RestoreSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore succeeded: True", audit.RestoreSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPatchPreviewAsync_VerificationFailureRecordsRestoreFailureWhenRestoreCannotComplete()
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
        var preview = CreatePreview(context.Root, "A.cs");

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

        var result = await context.Runner.ApplyPatchPreviewAsync(preview, profile);

        Assert.True(result.Applied);
        Assert.False(result.VerificationSucceeded);
        Assert.False(result.VerificationPassed);
        Assert.True(result.RestoreAttempted);
        Assert.False(result.RestoreSucceeded);
        Assert.NotEmpty(result.RestoreMessage);
        Assert.Contains("snapshot", result.RestoreMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("after", File.ReadAllText(Path.Combine(context.Root, "A.cs")));

        var audit = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.True(audit.Success);
        Assert.False(audit.VerificationSucceeded);
        Assert.True(audit.RestoreAttempted);
        Assert.False(audit.RestoreSucceeded);
        Assert.Contains("Restore attempted: True", audit.RestoreSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restore succeeded: False", audit.RestoreSummary, StringComparison.OrdinalIgnoreCase);
    }

    private static RunnerContext CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-mode-runner-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "A.cs"), "before");
        InitializeGitRepository(root);

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
        return new RunnerContext(root, runner, coderConsoleService, policyService, auditService);
    }

    private static AgentModeProfile CreateToolRunnerProfile(string policyId = "ToolRunner")
    {
        return new AgentModeProfile
        {
            Id = "tool-runner",
            Mode = AgentMode.ToolRunner,
            Name = "Tool Runner",
            Model = "legacy-model",
            PolicyId = policyId
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

    private static LocalCoderPatchPreview CreatePreview(string root, string relativePath, IReadOnlyList<string>? verificationCommands = null)
    {
        return new LocalCoderPatchPreview
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectPath = root,
            Task = $"Update {relativePath}",
            PatchText = $"diff --git a/{relativePath} b/{relativePath}",
            FileChanges =
            [
                new PatchFileChange
                {
                    RelativePath = relativePath,
                    OldContent = "before",
                    NewContent = "after"
                }
            ],
            VerificationCommands = verificationCommands ?? []
        };
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
        AgentExecutionPolicyService PolicyService,
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
}
