using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PlannerContextSelectionServiceTests
{
    private readonly PlannerContextSelectionService service = new();

    [Fact]
    public void SelectContextFiles_ResolvesEditableInstructionAndMissingFiles()
    {
        var index = new ProjectKnowledgeIndex
        {
            Items =
            [
                new ProjectKnowledgeItem { RelativePath = "Components/Pages/Coder.razor" },
                new ProjectKnowledgeItem { RelativePath = "Services/AGENTS.md" },
                new ProjectKnowledgeItem { RelativePath = "Components/AGENTS.md" }
            ]
        };

        var result = service.SelectContextFiles(
            new PlannerResult
            {
                TargetFiles =
                [
                    "Components/Pages/Coder.razor",
                    "Services/AGENTS.md",
                    "Missing/PlannerTarget.cs"
                ],
                InstructionFiles =
                [
                    "Components/AGENTS.md",
                    "Services/AGENTS.md"
                ],
                Rules =
                [
                    "Keep changes minimal."
                ]
            },
            index);

        Assert.Equal(["Components/Pages/Coder.razor"], result.EditableFiles);
        Assert.Equal(["Services/AGENTS.md", "Components/AGENTS.md"], result.InstructionFiles);
        Assert.Equal(["Missing/PlannerTarget.cs"], result.MissingFiles);
        Assert.Contains(result.Warnings, warning => warning.Contains("AGENTS.md file 'Services/AGENTS.md' was treated as read-only instruction context.", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.IsPatchBuilderBlocked);
        Assert.Equal(["Keep changes minimal."], result.Rules);
    }

    [Fact]
    public void SelectContextFiles_BlocksPatchBuilderWhenNoEditableFilesResolve()
    {
        var index = new ProjectKnowledgeIndex
        {
            Items =
            [
                new ProjectKnowledgeItem { RelativePath = "Services/AGENTS.md" }
            ]
        };

        var result = service.SelectContextFiles(
            new PlannerResult
            {
                TargetFiles = ["Services/AGENTS.md"],
                InstructionFiles = ["Services/AGENTS.md"]
            },
            index);

        Assert.Empty(result.EditableFiles);
        Assert.True(result.IsPatchBuilderBlocked);
        Assert.Contains(result.Warnings, warning => warning.Contains("PatchBuilder is blocked", StringComparison.OrdinalIgnoreCase));
    }
}
