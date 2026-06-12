using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchContextCoverageAnalyzerTests
{
    [Fact]
    public void Analyze_CategorizesDiffFilesAgainstContext()
    {
        var result = PatchContextCoverageAnalyzer.Analyze(
            """
            diff --git a/Components/Pages/Coder.razor b/Components/Pages/Coder.razor
            diff --git a/Components/Coder/CoderPromptPanel.razor b/Components/Coder/CoderPromptPanel.razor
            diff --git a/Services/AuthService.cs b/Services/AuthService.cs
            """,
            [new LocalCoderFileContext { RelativePath = "Components/Pages/Coder.razor" }]);

        Assert.Equal(3, result.Files.Count);
        Assert.Equal(PatchContextCoverageCategory.ContextFile, result.Files[0].Category);
        Assert.Equal(PatchContextCoverageCategory.RelatedFile, result.Files[1].Category);
        Assert.Equal(PatchContextCoverageCategory.UnknownFile, result.Files[2].Category);
        Assert.True(result.HasFilesOutsideContext);
    }

    [Fact]
    public void Analyze_IncreasesRiskForFilesOutsideContextAndSensitiveFiles()
    {
        var result = PatchContextCoverageAnalyzer.Analyze(
            """
            diff --git a/Program.cs b/Program.cs
            diff --git a/appsettings.Development.json b/appsettings.Development.json
            diff --git a/Services/AuthenticationService.cs b/Services/AuthenticationService.cs
            diff --git a/Data/AppDbContext.cs b/Data/AppDbContext.cs
            """,
            []);

        Assert.Equal(8, result.RiskScore);
        Assert.All(result.Files, file =>
            Assert.Contains("Modified file was not provided as context.", file.RiskReasons));
        Assert.Contains("Application startup file.", result.Files[0].RiskReasons);
        Assert.Contains("Application settings file.", result.Files[1].RiskReasons);
        Assert.Contains("Authentication code.", result.Files[2].RiskReasons);
        Assert.Contains("Database context.", result.Files[3].RiskReasons);
    }
}
