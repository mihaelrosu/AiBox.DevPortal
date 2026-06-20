using System.Net;
using System.Net.Http;
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

public sealed class AgentOrchestrationSafetyServiceTests
{
    [Fact]
    public async Task CriticalRisk_BlocksAutoApply()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-1",
            "Critical task",
            CreateSlice("slice-1", RiskLevel.Critical),
            ["Program.cs", "Services/AuthService.cs", "Data/AppDbContext.cs"]);

        Assert.True(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Equal(RiskLevel.Critical, report.HighestRiskLevel);
        Assert.Contains(report.Reasons, reason => reason.Contains("cannot be applied", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("blocked auto apply", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HighRisk_RequiresApproval()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-2",
            "High task",
            CreateSlice("slice-2", RiskLevel.High),
            ["Docs/Change.md"]);

        Assert.False(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Equal(RiskLevel.High, report.HighestRiskLevel);
        Assert.Contains("manual approval", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProgramCsChanges_RequireApproval()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-3",
            "Startup change",
            CreateSlice("slice-3", RiskLevel.Low),
            ["Program.cs"]);

        Assert.False(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Contains(report.Reasons, reason => reason.Contains("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LowRisk_IsAllowed()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-4",
            "Docs task",
            CreateSlice("slice-4", RiskLevel.Low),
            ["Docs/README.md"]);

        Assert.False(report.BlocksAutoApply);
        Assert.False(report.RequiresManualApproval);
        Assert.Equal(RiskLevel.Low, report.HighestRiskLevel);
        Assert.Contains("allowed auto apply", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_IsSkipped_WhenSafetyBlocks()
    {
        var root = CreateTempProjectRoot();

        try
        {
            WriteProjectFile(root);
            var harness = CreateHarness(
                root,
                BuildOllamaResponse(BuildPatchOperationsJson(
                    new PatchOperationFixture("Program.cs", "Console.WriteLine(\"one\");"),
                    new PatchOperationFixture("Services/AuthService.cs", """
                        namespace AiBox.DevPortal.Services;

                        public sealed class AuthService
                        {
                        }
                        """),
                    new PatchOperationFixture("Data/AppDbContext.cs", """
                        namespace AiBox.DevPortal.Data;

                        public sealed class AppDbContext
                        {
                        }
                        """),
                    new PatchOperationFixture("Data/Migrations/202606190001_AddUsers.cs", """
                        namespace AiBox.DevPortal.Data.Migrations;

                        public sealed class AddUsers
                        {
                        }
                        """),
                    new PatchOperationFixture("Services/Support/FeatureFlagService.cs", """
                        namespace AiBox.DevPortal.Services.Support;

                        public sealed class FeatureFlagService
                        {
                        }
                        """),
                    new PatchOperationFixture("Security/Secrets.cs", """
                        namespace AiBox.DevPortal.Security;

                        public sealed class Secrets
                        {
                        }
                        """),
                    new PatchOperationFixture("Security/Credentials.cs", """
                        namespace AiBox.DevPortal.Security;

                        public sealed class Credentials
                        {
                        }
                        """),
                    new PatchOperationFixture("Controllers/AdminController.cs", """
                        namespace AiBox.DevPortal.Controllers;

                        public sealed class AdminController
                        {
                        }
                        """),
                    new PatchOperationFixture("Docs/README.md", "# docs"),
                    new PatchOperationFixture("Docs/More.md", "# more"),
                    new PatchOperationFixture("Extra/Another.cs", """
                        namespace AiBox.DevPortal.Extra;

                        public sealed class Another
                        {
                        }
                        """))),
                BuildOllamaResponse("Looks good."));

            var run = await harness.Service.RunOrchestrationAsync(
                "Safety blocked orchestration",
                """
                Create:
                Program.cs
                Services/AuthService.cs
                Data/AppDbContext.cs
                Data/Migrations/202606190001_AddUsers.cs
                Services/Support/FeatureFlagService.cs
                Security/Secrets.cs
                Security/Credentials.cs
                Controllers/AdminController.cs
                Docs/README.md
                Docs/More.md
                Extra/Another.cs
                """,
                commitAndSync: false,
                approveHighRiskApply: true);

            Assert.Equal(AgentOrchestrationStatus.Failed, run.Status);
            Assert.True(run.SafetyBlocksAutoApply);
            Assert.True(run.SafetyRequiresManualApproval);
            Assert.NotEmpty(run.SafetyReportId);
            Assert.Contains(run.SafetyReasons, reason => reason.Contains("cannot be applied", StringComparison.OrdinalIgnoreCase));
            Assert.False(run.ApplySucceeded);
            Assert.Equal(AgentOrchestrationStatus.Failed, run.Steps[4].Status);
            Assert.Equal(AgentOrchestrationStatus.Skipped, run.Steps[5].Status);
            Assert.Contains("cannot be applied", run.ApplyRiskGateMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static Context CreateContext()
    {
        var root = CreateTempProjectRoot();
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var environment = new TestWebHostEnvironment(root);
        var service = new AgentOrchestrationSafetyService(environment);
        return new Context(root, service);
    }

    private static TaskPlanSlice CreateSlice(string sliceId, RiskLevel riskLevel)
    {
        return new TaskPlanSlice
        {
            Id = sliceId,
            PlanId = "plan-1",
            Title = sliceId,
            Goal = sliceId,
            Description = sliceId,
            RiskLevel = riskLevel
        };
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
        var checkpointService = new AgentOrchestrationCheckpointService(environment);
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
            checkpointService,
            safetyService,
            timelineService,
            gitSyncService);

        return new TestHarness(service, approvalQueueService);
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
        var root = Path.Combine(Path.GetTempPath(), $"agent-orchestration-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteProjectFile(string root)
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

        File.WriteAllText(
            Path.Combine(root, "Program.cs"),
            """
            Console.WriteLine("Hello from orchestration safety tests");
            """);
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record PatchOperationFixture(string FilePath, string NewText);

    private sealed record Context(string Root, AgentOrchestrationSafetyService Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            DeleteTempProjectRoot(Root);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestHarness(AgentOrchestrationService Service, HumanApprovalQueueService QueueService);

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
