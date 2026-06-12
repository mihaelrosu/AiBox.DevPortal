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
    public void Evaluate_ProtectedPath_BlocksIntent()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Update the patch apply flow.",
            AllowedPatchScope = PatchScopeMode.AnyProjectFile
        });

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
    }

    [Fact]
    public void Evaluate_InScopeUpdate_MatchesIntent()
    {
        var intent = PatchIntentService.BuildIntent(new LocalCoderRequest
        {
            ProjectPath = "/project",
            Task = "Update the verify card layout.",
            AllowedPatchScope = PatchScopeMode.AnyProjectFile
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
    }
}
