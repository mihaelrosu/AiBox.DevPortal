using System.Net;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentOrchestrationServiceTests
{
    [Fact]
    public async Task RunOrchestrationAsync_Succeeds_EndToEnd()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            InitializeGitRepository(root);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))),
                BuildOllamaResponse("The patch looks good. Apply it."));

            var run = await harness.Service.RunOrchestrationAsync(
                "Implement orchestration",
                "Create: Models/OrchestrationExample.cs",
                commitAndSync: true);

            Assert.Equal(AgentOrchestrationStatus.Completed, run.Status);
            Assert.True(run.ApplySucceeded);
            Assert.Contains("Applied successfully", run.ApplyMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Single(run.AppliedSliceIds);
            Assert.Contains("Models/OrchestrationExample.cs", run.AppliedFiles, StringComparer.OrdinalIgnoreCase);
            Assert.NotEmpty(run.ApplyAuditIds);
            Assert.True(run.CommitAttempted);
            Assert.True(run.CommitSucceeded);
            Assert.False(string.IsNullOrWhiteSpace(run.CommitHash));
            Assert.True(run.PushAttempted);
            Assert.True(run.PushSucceeded);
            Assert.Contains("Git sync completed successfully", run.GitMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(6, run.Steps.Count);
            Assert.All(run.Steps, step => Assert.Equal(AgentOrchestrationStatus.Completed, step.Status));
            Assert.All(run.Steps, step =>
            {
                Assert.True(step.StartedAtUtc != default);
                Assert.NotNull(step.CompletedAtUtc);
                Assert.True(step.DurationMs >= 0);
                Assert.True(step.Success);
                Assert.True(string.IsNullOrWhiteSpace(step.ErrorMessage));
            });
            Assert.True(File.Exists(Path.Combine(root, "Models", "OrchestrationExample.cs")));
            Assert.True(File.Exists(Path.Combine(root, "Data", "agent-orchestration-runs.json")));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_CommitAndSyncFalse_SkipsGitStep()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))),
                BuildOllamaResponse("The patch looks good. Apply it."));

            var run = await harness.Service.RunOrchestrationAsync(
                "Skip git sync",
                "Create: Models/OrchestrationExample.cs",
                commitAndSync: false);

            Assert.Equal(AgentOrchestrationStatus.Completed, run.Status);
            Assert.False(run.CommitAttempted);
            Assert.False(run.CommitSucceeded);
            Assert.False(run.PushAttempted);
            Assert.False(run.PushSucceeded);
            Assert.Contains("skipped", run.GitMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(6, run.Steps.Count);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[5].Status);
            Assert.Contains("CommitAndSync is false", run.Steps[5].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_PlannerFailure_FailsEarly()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var harness = CreateHarness(root);

            var run = await harness.Service.RunOrchestrationAsync(
                "Planner failure",
                string.Empty,
                commitAndSync: false);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.Equal(6, run.Steps.Count);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[0].Status);
            Assert.Contains("Task request is required", run.Steps[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.All(run.Steps.Skip(1), step => Assert.Equal(AgentOrchestrationStatus.Skipped, step.Status));
            Assert.False(run.ApplySucceeded);
            Assert.False(run.CommitAttempted);
            Assert.False(run.PushAttempted);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_VerifierFailure_SkipsRemainingSteps()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: false);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))));

            var run = await harness.Service.RunOrchestrationAsync(
                "Verifier failure",
                "Create: Models/OrchestrationExample.cs",
                commitAndSync: false);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[0].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[1].Status);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[2].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[3].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[5].Status);
            Assert.False(run.ApplySucceeded);
            Assert.Contains("Skipped because orchestration failed", run.Steps[3].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Skipped because orchestration failed", run.Steps[4].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Skipped because orchestration failed", run.Steps[5].Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(run.CommitAttempted);
            Assert.False(run.PushAttempted);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_HighRiskApply_PausesAndCreatesApprovalRequest()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var riskyRequest = """
                Create:
                Program.cs
                Services/AuthService.cs
                Data/AppDbContext.cs
                Services/DependencyInjectionService.cs
                """;
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Program.cs",
                        """
                        Console.WriteLine("High risk update");
                        """),
                    new PatchOperationFixture(
                        "Services/AuthService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class AuthService
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Data/AppDbContext.cs",
                        """
                        namespace AiBox.DevPortal.Data;

                        public sealed class AppDbContext
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Services/DependencyInjectionService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class DependencyInjectionService
                        {
                        }
                        """))),
                BuildOllamaResponse("The patch is valid and should be reviewed."));

            var run = await harness.Service.RunOrchestrationAsync(
                "High risk blocked",
                riskyRequest,
                commitAndSync: false,
                approveHighRiskApply: false);

            Assert.Equal(AgentOrchestrationStatus.Paused, run.Status);
            Assert.Equal(RiskLevel.High, run.SafetyHighestRiskLevel);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[0].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[1].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[2].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[3].Status);
            Assert.Equal(AgentOrchestrationStatus.Paused, run.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Pending, run.Steps[5].Status);
            Assert.False(run.ApplySucceeded);
            Assert.Contains("manual approval", run.ApplyRiskGateMessage, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(run.HumanApprovalRequestId);
            Assert.True(run.HumanApprovalPending);
            Assert.NotEmpty(run.SafetyReportId);

            var safetyReport = await harness.SafetyService.GetLatestForRunAsync(run.Id);
            Assert.NotNull(safetyReport);
            Assert.Equal(run.SafetyReportId, safetyReport!.Id);
            Assert.True(safetyReport.RequiresManualApproval);
            Assert.False(safetyReport.BlocksAutoApply);
            Assert.Contains(safetyReport.Reasons, reason => reason.Contains("Program.cs", StringComparison.OrdinalIgnoreCase));

            var approvalRequest = Assert.Single(await harness.QueueService.GetPendingAsync(10));
            Assert.Equal(run.Id, approvalRequest.RunId);
            Assert.Contains("Program.cs", approvalRequest.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(run.CommitAttempted);
            Assert.False(run.PushAttempted);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_HumanApproval_ApproveResumesOrchestration()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var riskyRequest = """
                Create:
                Program.cs
                Services/AuthService.cs
                Data/AppDbContext.cs
                Services/DependencyInjectionService.cs
                """;
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Program.cs",
                        """
                        Console.WriteLine("High risk update");
                        """),
                    new PatchOperationFixture(
                        "Services/AuthService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class AuthService
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Data/AppDbContext.cs",
                        """
                        namespace AiBox.DevPortal.Data;

                        public sealed class AppDbContext
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Services/DependencyInjectionService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class DependencyInjectionService
                        {
                        }
                        """))),
                BuildOllamaResponse("The patch is valid and should be reviewed."));

            var pausedRun = await harness.Service.RunOrchestrationAsync(
                "High risk approval",
                riskyRequest,
                commitAndSync: false,
                approveHighRiskApply: false);

            Assert.Equal(AgentOrchestrationStatus.Paused, pausedRun.Status);
            Assert.True(pausedRun.HumanApprovalPending);
            Assert.Equal(RiskLevel.Low, pausedRun.SafetyHighestRiskLevel);
            Assert.False(pausedRun.SafetyBlocksAutoApply);
            Assert.True(pausedRun.SafetyRequiresManualApproval);
            Assert.NotEmpty(pausedRun.SafetyReportId);

            var safetyReport = await harness.SafetyService.GetLatestForRunAsync(pausedRun.Id);
            Assert.NotNull(safetyReport);
            Assert.Equal(pausedRun.SafetyReportId, safetyReport!.Id);
            Assert.Equal(RiskLevel.Low, safetyReport.HighestRiskLevel);
            Assert.True(safetyReport.RequiresManualApproval);
            Assert.False(safetyReport.BlocksAutoApply);

            var request = Assert.Single(await harness.QueueService.GetPendingAsync(10));
            Assert.Equal(pausedRun.Id, request.RunId);
            Assert.Equal(HumanApprovalRequestStatus.Pending, request.Status);

            await harness.QueueService.DecideAsync(request.Id, HumanApprovalDecision.Approved, "tester", "Approved for resume");

            var resumedRun = await harness.Service.ResumeOrchestrationAsync(pausedRun.Id);

            Assert.Equal(AgentOrchestrationStatus.Completed, resumedRun.Status);
            Assert.True(resumedRun.ApplySucceeded);
            Assert.False(resumedRun.HumanApprovalPending);
            Assert.NotEmpty(resumedRun.ApplyAuditIds);
            Assert.Equal(pausedRun.SafetyReportId, resumedRun.SafetyReportId);
            Assert.Equal(AgentOrchestrationStatus.Completed, resumedRun.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, resumedRun.Steps[5].Status);
            Assert.Contains("Applied successfully", resumedRun.ApplyMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_HumanApproval_RejectFailsOrchestration()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var riskyRequest = """
                Create:
                Program.cs
                Services/AuthService.cs
                Data/AppDbContext.cs
                Services/DependencyInjectionService.cs
                """;
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Program.cs",
                        """
                        Console.WriteLine("High risk update");
                        """),
                    new PatchOperationFixture(
                        "Services/AuthService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class AuthService
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Data/AppDbContext.cs",
                        """
                        namespace AiBox.DevPortal.Data;

                        public sealed class AppDbContext
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Services/DependencyInjectionService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class DependencyInjectionService
                        {
                        }
                        """))),
                BuildOllamaResponse("The patch is valid and should be reviewed."));

            var pausedRun = await harness.Service.RunOrchestrationAsync(
                "High risk rejection",
                riskyRequest,
                commitAndSync: false,
                approveHighRiskApply: false);

            Assert.Equal(AgentOrchestrationStatus.Paused, pausedRun.Status);
            Assert.True(pausedRun.HumanApprovalPending);
            Assert.NotEmpty(pausedRun.SafetyReportId);

            var request = Assert.Single(await harness.QueueService.GetPendingAsync(10));
            await harness.QueueService.DecideAsync(request.Id, HumanApprovalDecision.Rejected, "tester", "Rejected after review");

            var failedRun = await harness.Service.RejectHumanApprovalAsync(pausedRun.Id, "Rejected after review");

            Assert.Equal(AgentOrchestrationStatus.Failed, failedRun.Status);
            Assert.False(failedRun.HumanApprovalPending);
            Assert.False(failedRun.ApplySucceeded);
            Assert.Equal(pausedRun.SafetyReportId, failedRun.SafetyReportId);
            Assert.Contains("rejected", failedRun.PausedReason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(AgentOrchestrationStatus.Failed, failedRun.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, failedRun.Steps[5].Status);
            Assert.Empty(await harness.QueueService.GetPendingAsync(10));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_CriticalRiskAlwaysBlocked()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            var criticalRequest = """
                Create:
                Program.cs
                Services/AuthService.cs
                Data/AppDbContext.cs
                Services/DependencyInjectionService.cs
                Security/Secrets.cs
                Controllers/AdminController.cs
                Docs/README.md
                Docs/More.md
                Extra/Another.cs
                Extra/YetAnother.cs
                Extra/More.cs
                Extra/EvenMore.cs
                """;
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Program.cs",
                        """
                        Console.WriteLine("Critical risk update");
                        """),
                    new PatchOperationFixture(
                        "Services/AuthService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class AuthService
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Data/AppDbContext.cs",
                        """
                        namespace AiBox.DevPortal.Data;

                        public sealed class AppDbContext
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Services/DependencyInjectionService.cs",
                        """
                        namespace AiBox.DevPortal.Services;

                        public sealed class DependencyInjectionService
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Security/Secrets.cs",
                        """
                        namespace AiBox.DevPortal.Security;

                        public sealed class Secrets
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Controllers/AdminController.cs",
                        """
                        namespace AiBox.DevPortal.Controllers;

                        public sealed class AdminController
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Docs/README.md",
                        "# docs"),
                    new PatchOperationFixture(
                        "Docs/More.md",
                        "# more"),
                    new PatchOperationFixture(
                        "Extra/Another.cs",
                        """
                        namespace AiBox.DevPortal.Extra;

                        public sealed class Another
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Extra/YetAnother.cs",
                        """
                        namespace AiBox.DevPortal.Extra;

                        public sealed class YetAnother
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Extra/More.cs",
                        """
                        namespace AiBox.DevPortal.Extra;

                        public sealed class More
                        {
                        }
                        """),
                    new PatchOperationFixture(
                        "Extra/EvenMore.cs",
                        """
                        namespace AiBox.DevPortal.Extra;

                        public sealed class EvenMore
                        {
                        }
                        """))),
                BuildOllamaResponse("The patch is valid and should be reviewed."));

            var run = await harness.Service.RunOrchestrationAsync(
                "Critical risk blocked",
                criticalRequest,
                commitAndSync: false,
                approveHighRiskApply: true);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.Equal(RiskLevel.Critical, run.SafetyHighestRiskLevel);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[0].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[1].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[2].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[3].Status);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[5].Status);
            Assert.False(run.ApplySucceeded);
            Assert.Contains("cannot be applied", run.ApplyRiskGateMessage, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(run.ApplyAuditIds);
            Assert.NotEmpty(run.SafetyReportId);
            var safetyReport = await harness.SafetyService.GetLatestForRunAsync(run.Id);
            Assert.NotNull(safetyReport);
            Assert.True(safetyReport!.BlocksAutoApply);
            Assert.True(safetyReport.RequiresManualApproval);
            Assert.Empty(await harness.QueueService.GetPendingAsync(10));
            Assert.False(run.CommitAttempted);
            Assert.False(run.PushAttempted);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_ApplyNotRunAfterVerifierFailure()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: false);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))));

            var run = await harness.Service.RunOrchestrationAsync(
                "Verifier failure",
                "Create: Models/OrchestrationExample.cs",
                commitAndSync: false,
                approveHighRiskApply: true);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[0].Status);
            Assert.Equal(AgentOrchestrationStatus.Completed, run.Steps[1].Status);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[2].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[3].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[4].Status);
            Assert.False(run.ApplySucceeded);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task RunOrchestrationAsync_CommitFailure_MarksOrchestrationFailed()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root, validProgram: true);
            InitializeGitRepository(root, withPreCommitHook: true);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture(
                        "Models/OrchestrationExample.cs",
                        """
                        namespace AiBox.DevPortal.Models;

                        public sealed class OrchestrationExample
                        {
                            public string Message { get; set; } = "Hello";
                        }
                        """))),
                BuildOllamaResponse("The patch looks good. Apply it."));

            var run = await harness.Service.RunOrchestrationAsync(
                "Commit failure",
                "Create: Models/OrchestrationExample.cs",
                commitAndSync: true);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.True(run.ApplySucceeded);
            Assert.True(run.CommitAttempted);
            Assert.False(run.CommitSucceeded);
            Assert.False(run.PushAttempted);
            Assert.False(run.PushSucceeded);
            Assert.Contains("failed", run.GitMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(6, run.Steps.Count);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[5].Status);
            Assert.Contains("failed", run.Steps[5].ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(run.ApplyAuditIds);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static TestHarness CreateHarness(string root, params string[] responses)
    {
        var handler = new QueueHandler(responses);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost")
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiBox:LocalCoder:DefaultModel"] = "test-model",
                ["AiBox:LocalCoder:WorkspaceRoots:0"] = root
            })
            .Build();

        var environment = new TestWebHostEnvironment(root);
        var agentRunHistoryService = new AgentRunHistoryService(environment);
        var patchEditOperationService = new PatchEditOperationService(NullLogger<PatchEditOperationService>.Instance);
        var contextService = new LocalCoderContextService();
        var patchVerificationService = new PatchVerificationService(agentRunHistoryService);
        var repairService = new PatchPreviewRepairService(
            new OllamaService(httpClient),
            patchEditOperationService,
            configuration);
        var agentInstructionService = new AgentInstructionService(environment);
        var coderConsoleService = new CoderConsoleService(
            httpClient,
            configuration,
            environment,
            patchEditOperationService,
            contextService,
            patchVerificationService,
            new SelectedContextValidator(),
            repairService,
            agentInstructionService);
        var profileService = new AgentModeProfileService(environment);
        var patchPreviewPreparationService = new TaskSlicePatchPreviewPreparationService(contextService);
        var taskDecompositionService = new TaskDecompositionService(new RiskAnalysisService());
        var taskSliceVerificationService = new TaskSliceVerificationService(environment);
        var patchApprovalGateService = new PatchApprovalGateService();
        var patchPackageService = new PatchPackageService(environment, patchApprovalGateService);
        var patchRollbackService = new PatchRollbackService(patchPackageService, environment);
        var patchBackupService = new PatchBackupService(environment, NullLogger<PatchBackupService>.Instance);
        var taskSliceRollbackService = new TaskSliceRollbackService(patchRollbackService);
        var taskSliceApplyHistoryService = new TaskSliceApplyHistoryService(environment);
        var taskSliceApprovalService = new TaskSliceApprovalService(environment);
        var taskSliceApplyAuditService = new TaskSliceApplyAuditService(environment);
        var taskSliceApplyService = new TaskSliceApplyService(
            patchBackupService,
            patchPackageService,
            patchRollbackService,
            taskSliceRollbackService,
            taskSliceApplyHistoryService,
            taskSliceApprovalService,
            taskSliceApplyAuditService);
        var safetyService = new AgentOrchestrationSafetyService(environment);
        var timelineService = new AgentOrchestrationTimelineService(environment);
        var approvalQueueService = new HumanApprovalQueueService(environment, timelineService);
        var gitSyncService = new GitSyncService();
        var localCoderPatchService = new LocalCoderPatchService(
            new OllamaService(httpClient),
            new RepositoryFileContextService());
        var localCoderReviewService = new LocalCoderReviewService(new OllamaService(httpClient));

        var service = new AgentOrchestrationService(
            environment,
            taskDecompositionService,
            patchPreviewPreparationService,
            coderConsoleService,
            profileService,
            taskSliceVerificationService,
            localCoderPatchService,
            localCoderReviewService,
            patchPackageService,
            taskSliceApprovalService,
            taskSliceApplyAuditService,
            taskSliceApplyService,
            approvalQueueService,
            safetyService,
            timelineService,
            gitSyncService);

        return new TestHarness(service, approvalQueueService, safetyService);
    }

    private static string BuildPatchOperationsJson(params PatchOperationFixture[] operations)
    {
        return JsonSerializer.Serialize(new
        {
            operations = operations.Select(operation => new
            {
                filePath = operation.FilePath,
                operation = "create",
                newText = operation.NewText
            }).ToArray()
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string BuildOllamaResponse(string responseText)
    {
        return JsonSerializer.Serialize(new
        {
            response = responseText,
            done = true
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-orchestration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteProjectFile(string root, bool validProgram)
    {
        File.WriteAllText(
            Path.Combine(root, "OrchestrationTest.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        var entryPointPath = Path.Combine(root, "EntryPoint.cs");
        var entryPointText = validProgram
            ? """
              namespace AiBox.DevPortal.Tests;

              public static class EntryPoint
              {
                  public static void Main()
                  {
                      Console.WriteLine("Hello from orchestration tests");
                  }
              }
              """
            : """
              namespace AiBox.DevPortal.Tests;

              public static class EntryPoint
              {
                  public static void Main()
                  {
                      Console.WriteLine("Broken build"
                  }
              }
              """;
        File.WriteAllText(entryPointPath, entryPointText);
    }

    private static void InitializeGitRepository(string root, bool withPreCommitHook = false)
    {
        var remoteRoot = Path.Combine(Path.GetTempPath(), $"agent-orchestration-remote-{Guid.NewGuid():N}.git");
        RunGitCommand(Path.GetTempPath(), ["init", "--bare", remoteRoot]);

        RunGitCommand(root, ["init"]);
        RunGitCommand(root, ["checkout", "-b", "main"]);
        RunGitCommand(root, ["config", "user.email", "test@example.com"]);
        RunGitCommand(root, ["config", "user.name", "Test User"]);
        RunGitCommand(root, ["remote", "add", "origin", remoteRoot]);

        RunGitCommand(root, ["add", "-A"]);
        RunGitCommand(root, ["commit", "-m", "Initial baseline"]);
        RunGitCommand(root, ["push", "-u", "origin", "main"]);

        if (withPreCommitHook)
        {
            var hookDirectory = Path.Combine(root, ".git", "hooks");
            Directory.CreateDirectory(hookDirectory);
            var hookPath = Path.Combine(hookDirectory, "pre-commit");
            File.WriteAllText(hookPath, "#!/bin/sh\nexit 1\n");
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PatchOperationFixture(string FilePath, string NewText);

    private sealed record TestHarness(
        AgentOrchestrationService Service,
        HumanApprovalQueueService QueueService,
        AgentOrchestrationSafetyService SafetyService);

    private sealed class QueueHandler(string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No more queued responses are available.");
            }

            var payload = responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
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
}
