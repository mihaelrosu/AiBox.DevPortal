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
        Assert.Equal(["Components/Pages/Coder.razor", "Components/Coder/CoderPromptPanel.razor"], intent.AllowedPaths);
        Assert.Equal(PatchIntentChangeType.Move, intent.ExpectedChangeType);
        Assert.Equal("dotnet build", intent.VerificationCommand);
        Assert.Contains("Patch apply logic", intent.MustNotChange);
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
