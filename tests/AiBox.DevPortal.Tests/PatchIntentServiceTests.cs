using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchIntentServiceTests
{
    [Fact]
    public void BuildIntent_DerivesClearContract()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Move Verify button into its own card.",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" },
                new LocalCoderFileContext { RelativePath = "Components/Coder/CoderPromptPanel.razor" }
            ]
        });

        Assert.Equal("Move Verify button into its own card", intent.Goal);
        Assert.Equal(["Components/Pages/Coder.razor", "Components/Coder/CoderPromptPanel.razor"], intent.AllowedFiles);
        Assert.Equal(["Components/Pages/Coder.razor", "Components/Coder/CoderPromptPanel.razor"], intent.AllowedPaths);
        Assert.Equal(["Components/Coder/", "Components/Pages/"], intent.AllowedCreateFolders);
        Assert.Equal(PatchPrimaryIntent.Modify, intent.PrimaryIntent);
        Assert.Equal(PatchIntentChangeType.Move, intent.ExpectedChangeType);
        Assert.Equal("dotnet build", intent.VerificationCommand);
        Assert.Contains("Patch apply logic", intent.MustNotChange);
    }

    [Fact]
    public void BuildIntent_CreateTask_ExtractsTargetFile()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Create Models/ProjectKnowledgeIndex.cs",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Models/LocalCoderHistoryEntry.cs" }
            ]
        });

        Assert.Contains("Models/ProjectKnowledgeIndex.cs", intent.TargetCreatedFiles);
        Assert.Contains("Models/ProjectKnowledgeIndex.cs", intent.AllowedFiles);
        Assert.Contains("Models/LocalCoderHistoryEntry.cs", intent.AllowedFiles);
        Assert.Contains("Models/", intent.AllowedCreateFolders);
        Assert.Contains("Target created file(s):", PatchIntentService.BuildPromptText(intent), StringComparison.Ordinal);
        Assert.Contains("Allowed create folders:", PatchIntentService.BuildPromptText(intent), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractRequestedCreateFiles_CreateSection_ExtractsPaths()
    {
        var files = PatchIntentService.ExtractRequestedCreateFiles("""
            Create:
            - Models/TaskPlan.cs
            - Components/Coder/TaskPlanPreviewPanel.razor
            """);

        Assert.Equal(["Models/TaskPlan.cs", "Components/Coder/TaskPlanPreviewPanel.razor"], files);
    }

    [Fact]
    public void BuildIntent_CreateTargetInModels_OverridesAllowedCreateFolders()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Create Models/ProjectKnowledgeIndex.cs",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            AllowedCreateFolders = ["Components/"],
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Models/LocalCoderHistoryEntry.cs" }
            ]
        });

        Assert.Equal(["Models/"], intent.AllowedCreateFolders);
    }

    [Fact]
    public void BuildIntent_CreateTargetInNestedFolder_UsesNestedFolder()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Create Features/Admin/ProjectKnowledgeIndex.cs",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            AllowedCreateFolders = ["Models/"],
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        Assert.Equal(["Features/Admin/"], intent.AllowedCreateFolders);
    }

    [Fact]
    public void BuildIntent_CreateTargetsInMultipleFolders_UsesAllTargetFolders()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Create Models/ProjectKnowledgeIndex.cs and Features/Admin/ProjectAudit.cs",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            AllowedCreateFolders = ["Components/"],
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        Assert.Equal(["Features/Admin/", "Models/"], intent.AllowedCreateFolders);
    }

    [Fact]
    public void BuildIntent_CreateBulletList_ExtractsMultipleTargets()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = """
                   Create:
                   - Models/TaskPlan.cs
                   - Features/Admin/AdminPanel.razor
                   - Data/ProjectIndex.json
                   - App/App.csproj
                   """,
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        Assert.Equal(4, intent.AllowedCreateFolders.Count);
        Assert.Contains("App/", intent.AllowedCreateFolders);
        Assert.Contains("Data/", intent.AllowedCreateFolders);
        Assert.Contains("Features/Admin/", intent.AllowedCreateFolders);
        Assert.Contains("Models/", intent.AllowedCreateFolders);

        Assert.Equal(4, intent.TargetCreatedFiles.Count);
        Assert.Contains("App/App.csproj", intent.TargetCreatedFiles);
        Assert.Contains("Data/ProjectIndex.json", intent.TargetCreatedFiles);
        Assert.Contains("Features/Admin/AdminPanel.razor", intent.TargetCreatedFiles);
        Assert.Contains("Models/TaskPlan.cs", intent.TargetCreatedFiles);
    }

    [Fact]
    public void BuildIntent_NoCreateTargets_UsesExistingBehavior()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Update the verify card layout.",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            AllowedCreateFolders = ["Services/"],
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" },
                new LocalCoderFileContext { RelativePath = "Components/Coder/CoderPromptPanel.razor" }
            ]
        });

        Assert.Equal(["Services/"], intent.AllowedCreateFolders);
        Assert.DoesNotContain("Target created file(s):", PatchIntentService.BuildPromptText(intent), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Read file and add comment")]
    [InlineData("Inspect file and create method")]
    public void BuildIntent_ModificationVerb_WinsOverReadOnlyVerb(string task)
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = task,
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        Assert.Equal(PatchPrimaryIntent.Modify, intent.PrimaryIntent);
    }

    [Theory]
    [InlineData("Review file")]
    [InlineData("Summarize file")]
    public void BuildIntent_ReadOnlyVerb_MapsToReadOnly(string task)
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = task,
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        Assert.Equal(PatchPrimaryIntent.ReadOnly, intent.PrimaryIntent);
    }

    [Fact]
    public void Evaluate_ExplicitProtectedPath_BlocksIntent()
    {
        var intent = new PatchIntent
        {
            Goal = "Update the patch apply flow.",
            AllowedPaths = ["Services/PatchApplyService.cs"],
            ProtectedPaths = ["Services/PatchApplyService.cs"],
            AllowedScope = "Any Project File",
            ExpectedChangeType = PatchIntentChangeType.Update,
            VerificationCommand = "dotnet build"
        };

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/Services/PatchApplyService.cs b/Services/PatchApplyService.cs
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "Services/PatchApplyService.cs" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "Services/PatchApplyService.cs",
                        Status = PatchScopeStatus.InScope
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.DoesNotMatch, validation.Status);
        Assert.Contains(validation.Reasons, reason => reason.Contains("protected files", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Services/PatchApplyService.cs", validation.ProtectedFiles);
    }

    [Fact]
    public void Evaluate_InContextUpdate_MatchesIntent()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Update the verify card layout.",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/Components/Pages/Coder.razor b/Components/Pages/Coder.razor
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "Components/Pages/Coder.razor" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "Components/Pages/Coder.razor",
                        Status = PatchScopeStatus.InScope
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.MatchesIntent, validation.Status);
        Assert.Empty(validation.ProtectedFiles);
        Assert.Contains("Components/Pages/Coder.razor", validation.RequestedFiles);
        Assert.Contains("Components/Pages/Coder.razor", validation.ModifiedFiles);
    }

    [Fact]
    public void Evaluate_ExplicitCreateTarget_InRepresentedFolder_MatchesIntent()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Create Models/ProjectKnowledgeIndex.cs",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Models/LocalCoderHistoryEntry.cs" }
            ]
        });

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/Models/ProjectKnowledgeIndex.cs b/Models/ProjectKnowledgeIndex.cs
                        new file mode 100644
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "Models/ProjectKnowledgeIndex.cs", NewContent = "public sealed class ProjectKnowledgeIndex {}" }
            ],
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Models/LocalCoderHistoryEntry.cs" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "Models/ProjectKnowledgeIndex.cs",
                        Status = PatchScopeStatus.InScope,
                        IsCreate = true,
                        ContextRepresentativePath = "Models/"
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.MatchesIntent, validation.Status);
        Assert.Contains("Models/ProjectKnowledgeIndex.cs", validation.RequestedFiles);
        Assert.Contains("Models/ProjectKnowledgeIndex.cs", validation.ModifiedFiles);
    }

    [Fact]
    public void Evaluate_OutOfContextFile_DoesNotMatch()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Update the verify card layout.",
            AllowedPatchScope = PatchScopeMode.ContextFilesOnly,
            FileContexts =
            [
                new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }
            ]
        });

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/Services/AuthService.cs b/Services/AuthService.cs
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "Services/AuthService.cs" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "Services/AuthService.cs",
                        Status = PatchScopeStatus.OutOfScope
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.DoesNotMatch, validation.Status);
        Assert.Contains(validation.ProtectedFiles, path => path.Contains("AuthService.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_ProgramCs_Modified_IsProtected()
    {
        var intent = new PatchIntent
        {
            Goal = "Update startup logic.",
            AllowedPaths = ["Components/Pages/Coder.razor"],
            ProtectedPaths = [],
            AllowedScope = "Any Project File",
            ExpectedChangeType = PatchIntentChangeType.Update,
            VerificationCommand = "dotnet build"
        };

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/Program.cs b/Program.cs
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "Program.cs" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "Program.cs",
                        Status = PatchScopeStatus.InScope
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.DoesNotMatch, validation.Status);
        Assert.Contains(validation.ProtectedFiles, path => path.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluate_AppSettings_Modified_IsProtected()
    {
        var intent = new PatchIntent
        {
            Goal = "Update app configuration.",
            AllowedPaths = ["Components/Pages/Coder.razor"],
            ProtectedPaths = [],
            AllowedScope = "Any Project File",
            ExpectedChangeType = PatchIntentChangeType.Update,
            VerificationCommand = "dotnet build"
        };

        var preview = new LocalCoderPatchPreview
        {
            PatchText = """
                        diff --git a/appsettings.json b/appsettings.json
                        """,
            FileChanges =
            [
                new PatchFileChange { RelativePath = "appsettings.json" }
            ],
            ScopeAnalysis = new PatchScopeAnalysis
            {
                Files =
                [
                    new PatchScopeFileResult
                    {
                        RelativePath = "appsettings.json",
                        Status = PatchScopeStatus.InScope
                    }
                ]
            }
        };

        var validation = PatchIntentService.Evaluate(intent, preview);

        Assert.Equal(PatchIntentMatchStatus.DoesNotMatch, validation.Status);
        Assert.Contains(validation.ProtectedFiles, path => path.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase));
    }
}
