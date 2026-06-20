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

public sealed class AgentOrchestrationTimelineServiceTests
{
    [Fact]
    public async Task RecordEvent_SavesEvent()
    {
        var context = CreateContext();

        try
        {
            var saved = await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-1",
                EventType = AgentOrchestrationTimelineEventType.RunCreated,
                Message = "Run created.",
                Severity = AgentOrchestrationTimelineSeverity.Info,
                RelatedEntityId = "run-1"
            });

            Assert.False(string.IsNullOrWhiteSpace(saved.Id));
            Assert.Equal("run-1", saved.RunId);
            Assert.True(File.Exists(Path.Combine(context.Root, "Data", "agent-orchestration-timeline.json")));
        }
        finally
        {
            DeleteTempProjectRoot(context.Root);
        }
    }

    [Fact]
    public async Task ReadEventsByRun_ReturnsNewestFirst()
    {
        var context = CreateContext();

        try
        {
            await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-1",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                EventType = AgentOrchestrationTimelineEventType.StepStarted,
                StepName = "Planner",
                Message = "Started Planner.",
                Severity = AgentOrchestrationTimelineSeverity.Info,
                RelatedEntityId = "step-1"
            });

            await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-1",
                TimestampUtc = DateTime.UtcNow,
                EventType = AgentOrchestrationTimelineEventType.StepCompleted,
                StepName = "Planner",
                Message = "Completed Planner.",
                Severity = AgentOrchestrationTimelineSeverity.Info,
                RelatedEntityId = "step-1"
            });

            await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-2",
                TimestampUtc = DateTime.UtcNow,
                EventType = AgentOrchestrationTimelineEventType.RunCreated,
                Message = "Other run.",
                Severity = AgentOrchestrationTimelineSeverity.Info,
                RelatedEntityId = "run-2"
            });

            var events = await context.Service.GetByRunAsync("run-1");

            Assert.Equal(2, events.Count);
            Assert.Equal(AgentOrchestrationTimelineEventType.StepCompleted, events[0].EventType);
            Assert.Equal(AgentOrchestrationTimelineEventType.StepStarted, events[1].EventType);
        }
        finally
        {
            DeleteTempProjectRoot(context.Root);
        }
    }

    [Fact]
    public async Task FailedStep_CreatesErrorEvent()
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

            var events = await harness.TimelineService.GetByRunAsync(run.Id);

            Assert.Contains(events, item => item.EventType == AgentOrchestrationTimelineEventType.StepFailed &&
                                           item.Severity == AgentOrchestrationTimelineSeverity.Error);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task SafetyBlock_CreatesWarningOrErrorEvent()
    {
        var context = CreateContext();

        try
        {
            await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-1",
                EventType = AgentOrchestrationTimelineEventType.SafetyReviewGenerated,
                StepName = "Apply",
                Message = "Safety review requires manual approval.",
                Severity = AgentOrchestrationTimelineSeverity.Warning,
                RelatedEntityId = "report-1"
            });

            await context.Service.AddAsync(new AgentOrchestrationTimelineEvent
            {
                RunId = "run-1",
                EventType = AgentOrchestrationTimelineEventType.ApplySkipped,
                StepName = "Apply",
                Message = "Safety review blocked apply.",
                Severity = AgentOrchestrationTimelineSeverity.Error,
                RelatedEntityId = "slice-1"
            });

            var events = await context.Service.GetByRunAsync("run-1");

            Assert.Contains(events, item => item.EventType == AgentOrchestrationTimelineEventType.SafetyReviewGenerated);
            Assert.Contains(events, item => item.EventType == AgentOrchestrationTimelineEventType.ApplySkipped &&
                                           (item.Severity == AgentOrchestrationTimelineSeverity.Warning ||
                                            item.Severity == AgentOrchestrationTimelineSeverity.Error));
        }
        finally
        {
            DeleteTempProjectRoot(context.Root);
        }
    }

    private static Context CreateContext()
    {
        var root = CreateTempProjectRoot();
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        var environment = new TestWebHostEnvironment(root);
        var service = new AgentOrchestrationTimelineService(environment);
        return new Context(root, service);
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

        return new TestHarness(service, timelineService);
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

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-orchestration-timeline-{Guid.NewGuid():N}");
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

        var programPath = Path.Combine(root, "Program.cs");
        var programText = validProgram
            ? """
              Console.WriteLine("Hello from orchestration timeline tests");
              """
            : """
              Console.WriteLine("Broken build"
              """;
        File.WriteAllText(programPath, programText);
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string BuildOllamaResponse(string responseText)
    {
        return JsonSerializer.Serialize(new
        {
            response = responseText,
            done = true
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private sealed record PatchOperationFixture(string FilePath, string NewText);

    private sealed record Context(string Root, AgentOrchestrationTimelineService Service);

    private sealed record TestHarness(AgentOrchestrationService Service, AgentOrchestrationTimelineService TimelineService);

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
