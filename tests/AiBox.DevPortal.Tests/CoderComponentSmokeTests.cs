using AiBox.DevPortal.Components.Coder;
using AiBox.DevPortal.Components.Pages;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Browser;
using System.Net;
using System.Net.Http;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Radzen;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class CoderComponentSmokeTests : TestContext
{
    public CoderComponentSmokeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddRadzenComponents();
        RegisterPageServices();
    }
    [Fact]
    public void CoderPage_SuggestContext_Renders()
    {
        var cut = RenderComponent<Coder>();

        Assert.Contains("Suggest Context", cut.Markup, StringComparison.Ordinal);
    }
    [Fact]
    public void CoderPage_Renders()
    {
        var cut = RenderComponent<Coder>();

        Assert.Contains("Local Coder", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Configured model routes:", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Select context and describe the work", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("File Context", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Task slices", cut.Markup, StringComparison.Ordinal);

        // Use text that is always visible on first render:
        // Assert.Contains("History, audits, snapshots, and timeline", cut.Markup);
        // Assert.Contains("Latest 25 timeline items", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("0 files", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("0 characters", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("~0 tokens", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Clear All", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderPage_MissingProfiles_RendersProfilesTab()
    {
        var cut = RenderComponent<Coder>();

        Assert.Contains("No default coder route", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentProfilesPanel_MissingProfiles_RendersLoadingState()
    {
        var cut = RenderComponent<AgentProfilesPanel>();

        Assert.Contains("Agent Model Routing", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderProjectSelector_Renders()
    {
        var cut = RenderComponent<CoderProjectSelector>();

        Assert.Contains("Project path", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoderKnowledgePanel_RendersAgentInstructionsSection()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"coder-knowledge-panel-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Coder"));

            await File.WriteAllTextAsync(Path.Combine(projectRoot, "AGENTS.md"), "root instructions");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Components", "AGENTS.md"), "components instructions");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Components", "Coder", "AGENTS.md"), "coder instructions");

            var cut = RenderComponent<CoderKnowledgePanel>(parameters => parameters
                .Add(component => component.ProjectPath, projectRoot)
                .Add(component => component.SelectedFilePaths, ["Components/Coder/CoderKnowledgePanel.razor"]));

            cut.WaitForAssertion(() =>
            {
                Assert.Contains("Agent Instructions", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("✓ AGENTS.md", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("✓ Components/AGENTS.md", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("✓ Components/Coder/AGENTS.md", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("root instructions", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("components instructions", cut.Markup, StringComparison.Ordinal);
                Assert.Contains("coder instructions", cut.Markup, StringComparison.Ordinal);
            });
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CoderPromptPanel_Renders()
    {
        var cut = RenderComponent<CoderPromptPanel>();

        Assert.Contains("Create Plan", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderPatchQueuePanel_Renders()
    {
        var cut = RenderComponent<CoderPatchQueuePanel>();

        Assert.Contains("Patch Queue", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderExecutionPanel_Renders()
    {
        var cut = RenderComponent<CoderExecutionPanel>();

        Assert.Contains("Safe Commands", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderScheduledRunsPanel_Renders()
    {
        var cut = RenderComponent<CoderScheduledRunsPanel>();

        Assert.Contains("Scheduled Runs", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Refresh", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_Renders()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.Error, "Smoke test error"));

        Assert.Contains("Smoke test error", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_SliceDebug_RendersSliceDebugDetails()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.SliceDebugDetails,
            [
                "SliceId: abc123",
                "SliceTitle: Harden slice preview",
                "TargetFiles count: 2",
                "RelatedFiles count: 1",
                "SelectedContextFiles count: 3",
                "CreateTargets count: 1"
            ]));

        Assert.Contains("Slice Preview Debug", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("SliceId: abc123", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("CreateTargets count: 1", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_ValidationGuidance_RendersCategoryAndSuggestedFix()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.ValidationErrors, ["Patch preview only changes closing tags."])
            .Add(component => component.ValidationGuidance,
            [
                new PatchValidationGuidance(
                    PatchValidationCategory.HtmlStructureRisk,
                    "Patch preview only changes closing tags.",
                    "Use a complete HTML boundary.",
                    "insert_after a partial HTML boundary",
                    "replace the complete HTML block")
            ]));

        Assert.Contains("Safer Patch Suggestions", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("HtmlStructureRisk", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Use a complete HTML boundary.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Original operation", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Safer operation", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_PatchDebug_RendersGrammarAndResponseDetails()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.ValidationErrors, ["The model returned an invalid patch operation."])
            .Add(component => component.OperationGrammarErrors, ["Exact target text was not found in context."])
            .Add(component => component.SuggestedTargetDiagnostics,
            [
                new PatchSuggestedTargetDiagnostic(
                    "Example.cs",
                    "replace",
                    "oldText",
                    "public void AlphaBeto()",
                    "public void AlphaBeta()",
                    [
                        new PatchSuggestedTargetMatch(3, "public void AlphaBeta()", 91.2)
                    ],
                    "For adding documentation, use insert_before with the class or member declaration as anchor.")
            ])
            .Add(component => component.SelectedSuggestedTarget,
            new PatchSuggestedTargetSelection(
                "Example.cs",
                "replace",
                "oldText",
                "public void AlphaBeto()",
                "public void AlphaBeta()",
                3,
                91.2))
            .Add(component => component.PromptAudit, new PatchPromptAudit
            {
                ContextFiles = ["Example.cs"],
                ContextCharactersLoaded = 1234,
                ContextTextCharactersSent = 1500,
                ContextTextFirst500Chars = "FIRST500",
                ContextTextLast500Chars = "LAST500"
            })
            .Add(component => component.PromptTargetResolution, new PatchPromptTargetResolution
            {
                TargetFound = true,
                Operation = "insert_before",
                FilePath = "Example.cs",
                TargetText = "public void AlphaBeta()",
                SurroundingContext = "public class Example",
                LineNumber = 3,
                MatchCount = 1,
                ErrorMessage = "Use insert_before on the class declaration."
            }));

        Assert.Contains("Patch Debug", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Prompt Audit", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Deterministic Target Resolution", cut.Markup, StringComparison.Ordinal);
    }

    private void RegisterPageServices()
    {
        var contentRootPath = Path.Combine(Path.GetTempPath(), $"coder-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentRootPath);
        Directory.CreateDirectory(Path.Combine(contentRootPath, "Data"));

        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(contentRootPath);
        environment.ApplicationName.Returns("AiBox.DevPortal.Tests");
        environment.EnvironmentName.Returns("Development");

        Services.AddLogging();
        Services.AddSingleton(environment);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Services.AddScoped<IAgentModeProfileService, AgentModeProfileService>();
        Services.AddScoped<AgentModelRoutingService>();
        Services.AddScoped<AgentModelRecommendationService>();
        Services.AddScoped<AgentModelBenchmarkService>();
        Services.AddScoped<AgentModelComparisonService>();
        Services.AddScoped<AgentDashboardService>();
        Services.AddScoped<AgentOrchestrationCheckpointService>();
        Services.AddScoped<ExecutionPolicyProfileService>();
        Services.AddScoped<AgentModelRouteService>();
        Services.AddScoped<AgentModelRouteHealthCheckService>();
        Services.AddScoped<AgentExecutionPolicyService>();
        Services.AddScoped<AgentRunTimelineService>();
        Services.AddScoped<PatchSafetySnapshotService>();
        Services.AddScoped<ScheduledAgentRunService>();
        Services.AddScoped<AgentOrchestrationService>();
        Services.AddScoped<AgentInstructionService>();
        Services.AddScoped<PlannerContextSelectionService>();
        Services.AddScoped<TaskSlicePatchPreviewPreparationService>();
        Services.AddScoped<TaskSliceVerificationService>();
        Services.AddScoped<TaskSliceRiskAnalysisService>();
        Services.AddScoped<TaskSliceVerificationLoopService>();
        Services.AddScoped<TaskSliceApplyHistoryService>();
        Services.AddScoped<TaskSliceApplyAuditService>();
        Services.AddScoped<PatchApplyAuditService>();
        Services.AddScoped<TaskSliceApprovalService>();
        Services.AddScoped<TaskSliceApplyService>();
        Services.AddScoped<TaskSliceRollbackService>();
        Services.AddScoped<TaskPlanDependencyGraphService>();
        Services.AddScoped<TaskPlanApplyService>();
        Services.AddScoped<TaskDecompositionService>();
        Services.AddScoped<TaskSliceExecutionService>();
        Services.AddScoped<ProjectHistoryIndexService>();
        Services.AddScoped<RiskAnalysisService>();

        Services.AddScoped<IClipboardService, ClipboardService>();
        Services.AddScoped<IAgentRegistryService, AgentRegistryService>();
        Services.AddScoped<IAgentRunHistoryService, AgentRunHistoryService>();
        Services.AddSingleton(Substitute.For<IAgentModeRunner>());
        Services.AddScoped<IPatchPackageService, PatchPackageService>();
        Services.AddScoped<IPatchApplyService, PatchApplyService>();
        Services.AddScoped<IPatchRollbackService, PatchRollbackService>();
        Services.AddScoped<IPatchBackupService, PatchBackupService>();
        Services.AddScoped<IPatchApprovalGateService, PatchApprovalGateService>();
        Services.AddScoped<ILocalCoderContextService, LocalCoderContextService>();
        Services.AddScoped<ILocalCoderVerificationProfileService, LocalCoderVerificationProfileService>();
        Services.AddScoped<AiBox.DevPortal.Services.ILocalCoderHistoryService, AiBox.DevPortal.Services.LocalCoderHistoryService>();
        Services.AddScoped<IProjectKnowledgeIndexService, ProjectKnowledgeIndexService>();
        Services.AddSingleton(Substitute.For<ILocalCoderPatchService>());
        Services.AddSingleton(Substitute.For<ILocalCoderReviewService>());
        Services.AddSingleton(Substitute.For<IOllamaService>());
        Services.AddSingleton(Substitute.For<ILocalLlmService>());
        Services.AddSingleton(Substitute.For<IPromptEnhancerService>());
        Services.AddSingleton<IHttpClientFactory>(new SmokeHttpClientFactory());
        Services.AddScoped<PatchVerificationService>();
        Services.AddScoped<SelectedContextValidator>();
        Services.AddScoped<PatchPreviewRepairService>();

        Services.AddSingleton(Substitute.For<ICoderConsoleService>());
        Services.AddSingleton(Substitute.For<IFileOperationService>());
        Services.AddSingleton(Substitute.For<IFileSearchService>());
        Services.AddSingleton(Substitute.For<IGitOperationService>());
        Services.AddSingleton(Substitute.For<IDockerOperationService>());
        Services.AddSingleton(Substitute.For<IComfyUiOperationService>());
        Services.AddSingleton(Substitute.For<IExecutionPermissionProfileService>());
        Services.AddSingleton(Substitute.For<IExecutionEngineService>());
        Services.AddSingleton(Substitute.For<IWorkflowRegistryService>());
        Services.AddSingleton(Substitute.For<IWorkflowTemplateService>());
        Services.AddSingleton(Substitute.For<IWorkflowRunPreviewService>());
        Services.AddSingleton(Substitute.For<IWorkflowRunHistoryService>());
        Services.AddSingleton(Substitute.For<IDockerService>());
        Services.AddSingleton(Substitute.For<IGeneratedImageHistoryService>());
        Services.AddSingleton(Substitute.For<IImageToolService>());
        Services.AddSingleton(Substitute.For<IComfyUiService>());
        Services.AddSingleton(Substitute.For<ISdxlTextToImageService>());

        Services.AddScoped<ContextSuggestionService>();
    }

    private sealed class SmokeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SmokeHttpMessageHandler());
    }

    private sealed class SmokeHttpMessageHandler : HttpMessageHandler
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
