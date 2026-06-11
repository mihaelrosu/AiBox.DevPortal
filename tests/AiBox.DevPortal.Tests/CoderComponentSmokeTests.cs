using AiBox.DevPortal.Components.Coder;
using AiBox.DevPortal.Components.Pages;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Services.Browser;
using Bunit;
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
                    "Use a complete HTML boundary.")
            ]));

        Assert.Contains("Suggested Fix", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("HtmlStructureRisk", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Use a complete HTML boundary.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CoderHistoryPanel_Renders()
    {
        var cut = RenderComponent<CoderHistoryPanel>(parameters => parameters
            .Add(component => component.ShowAgentRunHistory, true)
            .Add(component => component.ShowTaskHistory, true));

        Assert.Contains("Agent Run History", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Task History", cut.Markup, StringComparison.Ordinal);
    }

    private void RegisterPageServices()
    {
        var coderConsoleService = Substitute.For<ICoderConsoleService>();
        coderConsoleService.GetWorkspaceRootsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([]));
        coderConsoleService.GetOllamaModelsAsync().Returns(Task.FromResult<IReadOnlyList<string>>([]));

        var profileService = Substitute.For<IAgentModeProfileService>();
        profileService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentModeProfile>>(CreateProfiles()));

        var runHistoryService = Substitute.For<IAgentRunHistoryService>();
        runHistoryService.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentRunRecord>>([]));

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

        Services.AddSingleton(coderConsoleService);
        Services.AddSingleton(Substitute.For<IAgentModeRunner>());
        Services.AddSingleton(profileService);
        Services.AddSingleton(runHistoryService);
        Services.AddSingleton(patchPackageService);
        Services.AddSingleton(Substitute.For<IPatchApplyService>());
        Services.AddSingleton(Substitute.For<IPatchRollbackService>());
        Services.AddSingleton(historyService);
        Services.AddSingleton(fileSearchService);
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
