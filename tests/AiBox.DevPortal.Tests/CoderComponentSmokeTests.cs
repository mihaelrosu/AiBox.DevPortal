using AiBox.DevPortal.Components.Coder;
using AiBox.DevPortal.Components.Pages;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Browser;
using Bunit;
using Microsoft.AspNetCore.Hosting;
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
    public void CoderPage_Renders()
    {
        var cut = RenderComponent<Coder>();

        Assert.Contains("Local Coder", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("File Context", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("0 files", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("0 characters", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("~0 tokens", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Clear All", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderPage_MissingProfiles_RendersProfilesTab()
    {
        var profileService = Services.GetRequiredService<IAgentModeProfileService>();
        profileService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentModeProfile>>([]));

        var cut = RenderComponent<Coder>();

        Assert.Contains("Agent Profiles", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderAgentProfilesPanel_MissingProfiles_RendersLoadingState()
    {
        var cut = RenderComponent<CoderAgentProfilesPanel>();

        Assert.Contains("Loading profiles...", cut.Markup, StringComparison.Ordinal);
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
    public void CoderDiffViewer_Renders()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.Error, "Smoke test error"));

        Assert.Contains("Smoke test error", cut.Markup, StringComparison.Ordinal);
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
                LineNumber = 3,
                TargetText = "public void AlphaBeta()",
                SurroundingContext = "1: class Example\n2: {\n3: public void AlphaBeta()\n4: }\n",
                MatchCount = 1
            })
            .Add(component => component.RawResponse, "{\"operations\":[]}")
            .Add(component => component.NormalizedResponse, "{\"operations\":[],\"errors\":[\"Exact target text was not found in context.\"]}")
            .Add(component => component.NormalizedDiff, string.Empty)
            .Add(component => component.PatchPreviewMetrics, new PatchPreviewMetricsSnapshot
            {
                Attempts = 3,
                SuccessfulPreviews = 2,
                FailedPreviews = 1,
                RepairedPreviews = 1
            }));

        Assert.Contains("Patch Debug", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Operation Grammar Errors", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Closest Matches", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Selected Suggested Target", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Use This Target", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Regenerate Patch With Suggested Target", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Clear Selected Suggested Target", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Fix Patch Preview", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Prompt Audit", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Characters loaded: 1,234", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Context text sent: 1,500 chars", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("First 500 chars of context sent to model", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Last 500 chars of context sent to model", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Deterministic Target Resolution", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Target found: Yes", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("File: Example.cs", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Line: 3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Raw Model Response", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Normalized Response", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Patch Preview Metrics", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Patch preview attempts: 3", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_PatchIntent_RendersPrimaryIntent()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.PatchPreview, new LocalCoderPatchPreview
            {
                Intent = new PatchIntent
                {
                    Goal = "Add XML documentation.",
                    PrimaryIntent = PatchPrimaryIntent.Modify,
                    AllowedScope = "Context Files Only",
                    ExpectedChangeType = PatchIntentChangeType.Add,
                    VerificationCommand = "dotnet build"
                }
            }));

        Assert.Contains("Primary Intent: Modify", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Expected change type: Add", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_PatchIntent_RendersCreateTargetFile()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.PatchPreview, new LocalCoderPatchPreview
            {
                Intent = new PatchIntent
                {
                    Goal = "Create Models/ProjectKnowledgeIndex.cs",
                    PrimaryIntent = PatchPrimaryIntent.Modify,
                    AllowedScope = "Context Files Only",
                    AllowedFiles = ["Models/LocalCoderHistoryEntry.cs", "Models/ProjectKnowledgeIndex.cs"],
                    TargetCreatedFiles = ["Models/ProjectKnowledgeIndex.cs"],
                    ExpectedChangeType = PatchIntentChangeType.Add,
                    VerificationCommand = "dotnet build"
                }
            }));

        Assert.Contains("Allowed files: Models/LocalCoderHistoryEntry.cs, Models/ProjectKnowledgeIndex.cs", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Target created file: Models/ProjectKnowledgeIndex.cs", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_PatchRepair_RendersSummary()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.ValidationErrors, ["Operation 'replace' for file 'Example.cs' requires a non-empty oldText."])
            .Add(component => component.PatchPreview, new LocalCoderPatchPreview
            {
                RepairSummary = new PatchPreviewRepairSummary
                {
                    OriginalOperation = "replace",
                    RepairAttempt = "insert_before",
                    RepairResult = "Success"
                }
            })
            .Add(component => component.RepairSummary, new PatchPreviewRepairSummary
            {
                OriginalOperation = "replace",
                RepairAttempt = "insert_before",
                RepairResult = "Success"
            }));

        Assert.Contains("Patch Repair", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Original Operation: replace", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Repair Attempt: insert_before", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Repair Result: Success", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderDiffViewer_CreateScopeDebug_RendersSelectedContextFolder()
    {
        var cut = RenderComponent<CoderDiffViewer>(parameters => parameters
            .Add(component => component.PatchPreview, new LocalCoderPatchPreview
            {
                ScopeAnalysis = new PatchScopeAnalysis
                {
                    Files =
                    [
                        new PatchScopeFileResult
                        {
                            RelativePath = "Models/ProjectKnowledgeIndex.cs",
                            Status = PatchScopeStatus.InScope,
                            IsCreate = true,
                            ContextRepresentativePath = "Models/LocalCoderHistoryEntry.cs"
                        }
                    ]
                }
            }));

        Assert.Contains("Selected Context Folder", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Models", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Created File", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Decision", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderHistoryPanel_Renders()
    {
        var cut = RenderComponent<CoderHistoryPanel>(parameters => parameters
            .Add(component => component.ShowAgentRunHistory, true)
            .Add(component => component.ShowTaskHistory, true)
            .Add(component => component.AgentRunHistory,
            [
                new AgentRunRecord
                {
                    ActionKey = "GeneratePatchPreview",
                    ProfileMode = AgentMode.PatchBuilder,
                    Model = "test-model",
                    Timestamp = DateTimeOffset.UtcNow,
                    Success = true,
                    UserRequest = "Generate a patch preview.",
                    PromptSent = "Prompt",
                    ResultText = "{}"
                }
            ])
            .Add(component => component.HistoryEntries,
            [
                new LocalCoderHistoryEntry
                {
                    Id = "history-1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    ActionType = LocalCoderHistoryActionType.GeneratePatchPreview,
                    ProjectPath = "/repo",
                    Model = "test-model",
                    Task = "Create Models/ProjectKnowledgeIndex.cs",
                    ContextFileCount = 1,
                    ContextEstimatedTokens = 42,
                    Message = "Patch preview created.",
                    Success = true
                }
            ]));

        Assert.Contains("Agent Run History", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Task History", cut.Markup, StringComparison.Ordinal);

        var taskHistoryTab = cut.FindAll("button")
            .First(button => button.TextContent.Contains("Task History", StringComparison.Ordinal));
        taskHistoryTab.Click();

        Assert.Contains("View", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Use Again", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Restore", cut.Markup, StringComparison.Ordinal);
    }

    private void RegisterPageServices()
    {
        var coderConsoleService = Substitute.For<ICoderConsoleService>();
        coderConsoleService.GetWorkspaceRootsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([]));
        coderConsoleService.GetOllamaModelsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var profileService = Substitute.For<IAgentModeProfileService>();
        profileService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentModeProfile>>(CreateProfiles()));

        var verificationProfileService = Substitute.For<ILocalCoderVerificationProfileService>();
        verificationProfileService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<LocalCoderVerificationProfile>>(
                [
                    new LocalCoderVerificationProfile
                    {
                        Id = "quick",
                        Name = "Quick",
                        Description = "Fast verification with a single build.",
                        Commands = ["dotnet build"]
                    }
                ]));
        verificationProfileService.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalCoderVerificationProfile?>(new LocalCoderVerificationProfile
            {
                Id = "quick",
                Name = "Quick",
                Description = "Fast verification with a single build.",
                Commands = ["dotnet build"]
            }));

        var runHistoryService = Substitute.For<IAgentRunHistoryService>();
        runHistoryService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>([]));

        var projectKnowledgeIndexService = Substitute.For<IProjectKnowledgeIndexService>();
        projectKnowledgeIndexService.GetLatestAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ProjectKnowledgeIndex?>(new ProjectKnowledgeIndex
            {
                ProjectPath = Path.GetTempPath(),
                Items = []
            }));
        projectKnowledgeIndexService.BuildAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ProjectKnowledgeIndex
            {
                ProjectPath = Path.GetTempPath(),
                Items = []
            }));

        var patchPackageService = Substitute.For<IPatchPackageService>();
        patchPackageService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PatchPackage>>([]));

        var historyService = Substitute.For<AiBox.DevPortal.Services.ILocalCoderHistoryService>();
        historyService.GetHistoryAsync(Arg.Any<int>())
            .Returns(Task.FromResult<IReadOnlyList<LocalCoderHistoryEntry>>([]));

        var fileSearchService = Substitute.For<IFileSearchService>();
        fileSearchService.SearchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<int>())
            .Returns(Task.FromResult(new List<FileSearchItem>()));

        var instructionEnvironment = Substitute.For<IWebHostEnvironment>();
        instructionEnvironment.ContentRootPath.Returns(Path.GetTempPath());

        Services.AddSingleton(coderConsoleService);
        Services.AddSingleton(Substitute.For<IAgentModeRunner>());
        Services.AddSingleton(profileService);
        Services.AddSingleton(verificationProfileService);
        Services.AddSingleton(runHistoryService);
        Services.AddSingleton(projectKnowledgeIndexService);
        Services.AddSingleton(new PlannerContextSelectionService());
        Services.AddSingleton(patchPackageService);
        Services.AddSingleton(Substitute.For<IPatchApplyService>());
        Services.AddSingleton(Substitute.For<IPatchRollbackService>());
        Services.AddSingleton(historyService);
        Services.AddSingleton(fileSearchService);
        Services.AddSingleton<ILocalCoderContextService, LocalCoderContextService>();
        Services.AddSingleton(new AgentInstructionService(instructionEnvironment));
        Services.AddSingleton(Substitute.For<IClipboardService>());
    }

    private static AgentModeProfile[] CreateProfiles()
    {
        return Enum.GetValues<AgentMode>()
            .Select(mode => new AgentModeProfile
            {
                Id = mode.ToString(),
                Mode = mode,
                Name = mode.ToString(),
                Model = "smoke-test-model",
                RulesSummary = "Smoke test profile"
            })
            .ToArray();
    }
}
