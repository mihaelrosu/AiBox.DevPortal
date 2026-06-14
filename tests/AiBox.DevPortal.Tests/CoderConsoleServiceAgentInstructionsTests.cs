using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class CoderConsoleServiceAgentInstructionsTests
{
    [Fact]
    public void BuildCreatePlanPrompt_IncludesAgentInstructionsSection()
    {
        var prompt = CoderConsoleService.BuildCreatePlanPrompt(
            new LocalCoderRequest
            {
                ProjectPath = "/project",
                Task = "Review the coder page"
            },
            "FILE: Components/Coder/CoderKnowledgePanel.razor",
            null,
            """
            FILE: AGENTS.md
            # Root instructions
            """);

        Assert.Contains("Relevant AGENTS.md files:", prompt, StringComparison.Ordinal);
        Assert.Contains("FILE: AGENTS.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGeneratePatchPreviewPrompt_IncludesAgentInstructionsSection()
    {
        var prompt = CoderConsoleService.BuildGeneratePatchPreviewPrompt(
            new LocalCoderRequest
            {
                ProjectPath = "/project",
                Task = "Update the coder page"
            },
            "- Components/Coder/CoderKnowledgePanel.razor",
            "FILE: Components/Coder/CoderKnowledgePanel.razor",
            "Context Files Only",
            "Goal: Update the coder page",
            agentInstructionsText: """
            FILE: AGENTS.md
            # Root instructions
            """);

        Assert.Contains("Relevant AGENTS.md files:", prompt, StringComparison.Ordinal);
        Assert.Contains("FILE: AGENTS.md", prompt, StringComparison.Ordinal);
    }
}
