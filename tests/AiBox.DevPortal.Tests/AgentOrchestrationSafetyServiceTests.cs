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
    public async Task SafePolicy_RequiresApproval_ForProgramCs()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-3",
            "Startup change",
            CreateSlice("slice-3", RiskLevel.Low),
            ["Program.cs"],
            "Safe");

        Assert.False(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Contains(report.Reasons, reason => reason.Contains("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BalancedPolicy_RespectsMaxChangedFiles()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-4",
            "Balanced task",
            CreateSlice("slice-4", RiskLevel.Low),
            [
                "Docs/README.md",
                "Docs/More.md",
                "Docs/Extra.md",
                "Docs/One.md",
                "Docs/Two.md",
                "Docs/Three.md",
                "Docs/Four.md",
                "Docs/Five.md",
                "Docs/Six.md",
                "Docs/Seven.md",
                "Docs/Eight.md"
            ],
            "Balanced");

        Assert.False(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Equal(RiskLevel.Low, report.HighestRiskLevel);
        Assert.Contains(report.Reasons, reason => reason.Contains("More than 10 changed files", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Balanced", report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AggressivePolicy_AllowsMoreChanges_ButStillBlocksCritical()
    {
        await using var context = CreateContext();

        var report = await context.Service.GenerateAsync(
            "run-5",
            "Aggressive task",
            CreateSlice("slice-5", RiskLevel.Critical),
            [
                "Program.cs",
                "Security/Secrets.cs",
                "Security/Identity/Users.cs",
                "Auth/LoginService.cs",
                "Docs/README.md",
                "Docs/More.md",
                "Docs/Extra.md",
                "Docs/One.md",
                "Docs/Two.md",
                "Docs/Three.md",
                "Docs/Four.md",
                "Docs/Five.md",
                "Docs/Six.md"
            ],
            "Aggressive");

        Assert.True(report.BlocksAutoApply);
        Assert.True(report.RequiresManualApproval);
        Assert.Equal(RiskLevel.Critical, report.HighestRiskLevel);
        Assert.Contains(report.Reasons, reason => reason.Contains("Critical risk", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Reasons, reason => reason.Contains("Program.cs changes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Reasons, reason => reason.Contains("More than", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Reasons, reason => reason.Contains("security changes", StringComparison.OrdinalIgnoreCase));
    }

    private static Context CreateContext()
    {
        var root = CreateTempProjectRoot();
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var environment = new TestWebHostEnvironment(root);
        var executionPolicyProfileService = new ExecutionPolicyProfileService(environment);
        var service = new AgentOrchestrationSafetyService(environment, executionPolicyProfileService);
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
        var executionPolicyProfileService = new ExecutionPolicyProfileService(environment);
        var safetyService = new AgentOrchestrationSafetyService(environment, executionPolicyProfileService);
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
            executionPolicyProfileService,
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
