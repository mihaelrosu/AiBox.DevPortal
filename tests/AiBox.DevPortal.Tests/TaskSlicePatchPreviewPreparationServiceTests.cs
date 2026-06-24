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
                    Description = """
                    Create:
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
    public async Task PrepareAsync_UsesTargetFilesAsCreateTargetsWhenNoLoadedContextsExist()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-target-files-create-targets-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));

            var result = await service.PrepareAsync(
                new TaskPlanSlice
                {
                    Id = "slice-2b",
                    Title = "Create new models",
                    TargetFiles =
                    [
                        "Models/ToolAgentRequest.cs",
                        "Models/ToolAgentResult.cs"
                    ]
                },
                projectRoot,
                []);

            Assert.True(result.CanGenerate);
            Assert.Equal(["Models/ToolAgentRequest.cs", "Models/ToolAgentResult.cs"], result.FileContexts.Select(context => context.RelativePath).ToArray());
            Assert.Contains(result.DebugDetails, detail => detail.Contains("CreateTargets", StringComparison.Ordinal));
            Assert.Contains(result.DebugDetails, detail => detail == "SelectedContextSource: CreateTargets");
            Assert.Contains("Create targets:", result.TaskText, StringComparison.Ordinal);
            Assert.Contains("Models/ToolAgentRequest.cs", result.TaskText, StringComparison.Ordinal);
            Assert.Contains("Models/ToolAgentResult.cs", result.TaskText, StringComparison.Ordinal);
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

    [Fact]
    public async Task PreparePromptContextAsync_ExtractsModifyAndCreateTargetsFromPromptSections()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-prompt-context-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Docs"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Components", "Coder.razor"), "coder");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Docs", "Readme.md"), "readme");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "appsettings.json"), "{}");

            var result = await service.PreparePromptContextAsync(
                """
                Create:
                - Services/NewFeature.cs

                Modify:
                - Components/Coder.razor

                Files:
                - Docs/Readme.md

                Context:
                - appsettings.json
                """,
                projectRoot);

            Assert.True(result.Prepared);
            Assert.Contains("Context prepared from task prompt.", result.Message);
            Assert.Equal(["Components/Coder.razor", "Docs/Readme.md", "appsettings.json"], result.SelectedFilePaths);
            Assert.Equal(["Services/"], result.AllowedCreateFolders);
            Assert.Equal(3, result.FileContexts.Count);
            Assert.Contains(result.DebugDetails, detail => detail == "PromptCreateTargets count: 1");
            Assert.Contains(result.DebugDetails, detail => detail == "PromptModifyTargets count: 1");
            Assert.Contains(result.DebugDetails, detail => detail == "PromptContextTargets count: 2");
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
    public async Task PreparePromptContextAsync_DetectsExplicitExistingFilePathsFromTaskText()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-explicit-paths-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Components", "Pages"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Services"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Models"));
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Components", "Pages", "Coder.razor"), "@page \"/coder\"");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Services", "TestFeatureService.cs"), "namespace Demo.Services;");
            await File.WriteAllTextAsync(Path.Combine(projectRoot, "Models", "TestFeature.cs"), "namespace Demo.Models;");

            var result = await service.PreparePromptContextAsync(
                """
                Update Components/Pages/Coder.razor, Services/TestFeatureService.cs, and Models/TestFeature.cs.
                """,
                projectRoot);

            Assert.True(result.Prepared);
            Assert.Equal(
                ["Components/Pages/Coder.razor", "Services/TestFeatureService.cs", "Models/TestFeature.cs"],
                result.SelectedFilePaths);
            Assert.Equal(3, result.FileContexts.Count);
            Assert.Empty(result.AllowedCreateFolders);
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
    public async Task PreparePromptContextAsync_DetectsCreateFoldersFromCreatePrompt()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-create-folders-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);

            var result = await service.PreparePromptContextAsync(
                """
                Create Models/X.cs and Services/X.cs.
                """,
                projectRoot);

            Assert.True(result.Prepared);
            Assert.Empty(result.SelectedFilePaths);
            Assert.Equal(["Models/", "Services/"], result.AllowedCreateFolders);
            Assert.Empty(result.FileContexts);
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
    public async Task PreparePromptContextAsync_DetectsCommonCreateFoldersFromKeywordTaskText()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"slice-preview-keyword-folders-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(projectRoot);

            var result = await service.PreparePromptContextAsync(
                """
                Create model
                Create service
                Add current time card
                """,
                projectRoot);

            Assert.True(result.Prepared);
            Assert.Empty(result.SelectedFilePaths);
            Assert.Equal(["Components/Pages/", "Models/", "Services/"], result.AllowedCreateFolders);
            Assert.Empty(result.FileContexts);
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
    public async Task PreparePromptContextAsync_CommandOnlyTaskDoesNotRequireFiles()
    {
        var result = await service.PreparePromptContextAsync(
            """
            dotnet build
            dotnet test --no-restore
            """,
            string.Empty);

        Assert.False(result.Prepared);
        Assert.Empty(result.SelectedFilePaths);
        Assert.Empty(result.AllowedCreateFolders);
    }

    [Fact]
    public async Task PreparePromptContextAsync_ReturnsClearMessageWhenNothingDetected()
    {
        var result = await service.PreparePromptContextAsync(
            """
            dotnet build
            dotnet test --no-restore
            """,
            string.Empty,
            includeNoMatchMessage: true);

        Assert.False(result.Prepared);
        Assert.Equal("No context files were detected from the task.", result.Message);
    }
}
