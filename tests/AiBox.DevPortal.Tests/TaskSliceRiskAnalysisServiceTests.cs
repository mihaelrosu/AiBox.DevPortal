using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceRiskAnalysisServiceTests
{
    [Fact]
    public void Analyze_LowRiskSlice_ProducesLowRisk()
    {
        var service = new TaskSliceRiskAnalysisService();

        var analysis = service.Analyze(new TaskPlanSlice
        {
            PlanId = "plan-1",
            Title = "Update docs",
            Goal = "Update docs",
            Description = "Update docs",
            TargetFiles = ["Docs/README.md"],
            VerificationCommands = ["dotnet build"]
        });

        Assert.Equal(TaskSliceRiskLevel.Low, analysis.RiskLevel);
        Assert.True(analysis.RiskScore <= 1);
        Assert.Empty(analysis.Reasons);
    }

    [Fact]
    public void Analyze_HighRiskSlice_ProducesCriticalRisk()
    {
        var service = new TaskSliceRiskAnalysisService();

        var analysis = service.Analyze(new TaskPlanSlice
        {
            PlanId = "plan-1",
            Title = "Security and database change",
            Goal = "Security and database change",
            Description = "Security and database change",
            TargetFiles =
            [
                "Program.cs",
                "appsettings.Development.json",
                "Services/AuthenticationService.cs",
                "Data/AppDbContext.cs",
                "Pages/Home.razor"
            ],
            AllowedChangeType = AllowedChangeType.Remove,
            Notes = "Urgent hotfix",
            VerificationCommands = []
        });

        Assert.Equal(TaskSliceRiskLevel.Critical, analysis.RiskLevel);
        Assert.True(analysis.RiskScore >= 7);
        Assert.Contains("Application startup file.", analysis.Reasons);
        Assert.Contains("Application settings file.", analysis.Reasons);
        Assert.Contains("Authentication or security code.", analysis.Reasons);
        Assert.Contains("Database or migration code.", analysis.Reasons);
        Assert.Contains("UI or markup file.", analysis.Reasons);
        Assert.Contains("Slice removes existing code or files.", analysis.Reasons);
        Assert.Contains("No verification commands were provided.", analysis.Reasons);
    }

    [Fact]
    public void AnalyzePlan_ReturnsAnalysisForEachSlice()
    {
        var service = new TaskSliceRiskAnalysisService();
        var plan = new TaskPlan
        {
            Id = "plan-1",
            OriginalRequest = "Risk",
            Slices =
            [
                new TaskPlanSlice { Id = "slice-1", Title = "Slice 1", TargetFiles = ["Docs/One.md"], VerificationCommands = ["dotnet build"] },
                new TaskPlanSlice { Id = "slice-2", Title = "Slice 2", TargetFiles = ["Program.cs"], VerificationCommands = ["dotnet build"] }
            ]
        };

        var analyses = service.Analyze(plan);

        Assert.Equal(2, analyses.Count);
        Assert.Equal("slice-1", analyses[0].SliceId);
        Assert.Equal("slice-2", analyses[1].SliceId);
    }
}
