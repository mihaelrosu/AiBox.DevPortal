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
                NewText = "public void AlphaBeto()",
                MatchLineNumber = 3,
                MatchConfidence = 91.2,
                Description = "Use insert_before on the class declaration."
            }));

        Assert.Contains("Patch Guidance", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Exact target text was not found in context.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("public void AlphaBeto()", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("public void AlphaBeta()", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Prompt Audit", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Context files loaded", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Prompt Target Resolution", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("insert_before", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Use insert_before on the class declaration.", cut.Markup, StringComparison.Ordinal);
    }
}
