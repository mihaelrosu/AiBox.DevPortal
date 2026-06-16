using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSlicePatchPreviewPreparationServiceTests
{
    private readonly TaskSlicePatchPreviewPreparationService service =
        new(new LocalCoderContextService());

    [Fact]
    public async Task PrepareAsync_PrioritizesTargetFilesOverRelatedAndSelectedContextFiles()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-target-priority-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Target.cs"), "target");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Related.cs"), "related");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Selected.cs"), "selected");

            var result = await service.PrepareAsync(
                new TaskPlanSlice
                {
                    Id = "slice-1",
                    Title = "Update target file",
                    TargetFiles = ["Target.cs"],
                    RelatedFiles = ["Related.cs"],
                    Notes = string.Empty
                },
                projectRoot,
                [
                    new LocalCoderFileContext
                    {
                        RelativePath = "Selected.cs",
                        FullPath = Path.Combine(projectRoot, "Selected.cs"),
                        Content = "selected"
                    }
                ]);

            Assert.True(result.CanGenerate);
            Assert.Equal("Target.cs", Assert.Single(result.FileContexts).RelativePath);
            Assert.Contains(result.DebugDetails, detail => detail == "SelectedContextSource: TargetFiles");
            Assert.Contains("Target files:", result.TaskText);
            Assert.Contains("Related files:", result.TaskText);
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
    public async Task PrepareAsync_UsesCreateTargetsWhenNoLoadedContextsExist()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-create-targets-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);

            var result = await service.PrepareAsync(
                new TaskPlanSlice
                {
                    Id = "slice-2",
                    Title = "Create a new model",
                    Notes = """
                    Create targets:
                    - Models/NewModel.cs
                    """
                },
                projectRoot,
                []);

            Assert.True(result.CanGenerate);
            var context = Assert.Single(result.FileContexts);
            Assert.Equal("Models/NewModel.cs", context.RelativePath);
            Assert.Contains(result.DebugDetails, detail => detail == "SelectedContextSource: CreateTargets");
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
    public async Task PrepareAsync_ReturnsFailureWhenNoValidTargetsExist()
    {
        var result = await service.PrepareAsync(
            new TaskPlanSlice
            {
                Id = "slice-3",
                Title = "Empty slice"
            },
            string.Empty,
            []);

        Assert.False(result.CanGenerate);
        Assert.Contains("no valid target files", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareAsync_RequiresSliceIdAndTitle()
    {
        var result = await service.PrepareAsync(
            new TaskPlanSlice
            {
                Id = string.Empty,
                Title = string.Empty
            },
            string.Empty,
            []);

        Assert.False(result.CanGenerate);
        Assert.Contains("id and title", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }
}
