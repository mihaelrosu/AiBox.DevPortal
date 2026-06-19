using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class RiskAnalysisServiceTests
{
    [Fact]
    public void Analyze_LowRiskSlice_ReturnsLowRisk()
    {
        var service = new RiskAnalysisService();

        var analysis = service.Analyze(new TaskPlanSlice
        {
            Id = "slice-1",
            PlanId = "plan-1",
            Title = "Docs update",
            Goal = "Docs update",
            Description = "Docs update",
            TargetFiles = ["Docs/README.md"],
            VerificationCommands = ["dotnet build"]
        });

        Assert.Equal(RiskLevel.Low, analysis.RiskLevel);
        Assert.Equal(0, analysis.TotalScore);
        Assert.False(analysis.RequiresManualApproval);
        Assert.Empty(analysis.Factors);
        Assert.Contains("no significant factors", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_HighRiskSlice_ReturnsCriticalRiskAndFactors()
    {
        var service = new RiskAnalysisService();

        var analysis = service.Analyze(new TaskPlanSlice
        {
            Id = "slice-2",
            PlanId = "plan-1",
            Title = "Security and infrastructure change",
            Goal = "Security and infrastructure change",
            Description = "Security and infrastructure change",
            TargetFiles =
            [
                "Program.cs",
                "Services/AuthService.cs",
                "Services/Identity/IdentityService.cs",
                "Data/AppDbContext.cs",
                "Data/Migrations/202406190001_AddUsers.cs",
                "Services/Support/FeatureFlagService.cs",
                "appsettings.json",
                "appsettings.Development.json",
                "Security/Secrets.cs",
                "Security/Credentials.cs",
                "Controllers/AdminController.cs"
            ],
            AllowedChangeType = AllowedChangeType.Remove,
            VerificationCommands = [],
            Notes = "critical hotfix"
        });

        Assert.Equal(RiskLevel.Critical, analysis.RiskLevel);
        Assert.True(analysis.TotalScore >= 81);
        Assert.True(analysis.RequiresManualApproval);
        Assert.Contains(analysis.Factors, factor => factor.Name == "Many affected files");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Program.cs changes");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Authentication or identity files");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Database or migration files");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Service or DI changes");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Delete or remove operations");
        Assert.Contains(analysis.Factors, factor => factor.Name == "Protected or security-sensitive files");
        Assert.Contains("Critical", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnalyzePlan_ReturnsPlanSummary()
    {
        var service = new RiskAnalysisService();
        var plan = new TaskPlan
        {
            Id = "plan-2",
            OriginalRequest = "Risk summary",
            Slices =
            [
                new TaskPlanSlice
                {
                    Id = "slice-1",
                    PlanId = "plan-2",
                    Title = "Low",
                    TargetFiles = ["Docs/README.md"],
                    VerificationCommands = ["dotnet build"]
                },
                new TaskPlanSlice
                {
                    Id = "slice-2",
                    PlanId = "plan-2",
                    Title = "High",
                    TargetFiles = ["Program.cs", "Services/AuthService.cs", "Security/Secrets.cs"],
                    AllowedChangeType = AllowedChangeType.Remove,
                    VerificationCommands = []
                }
            ]
        };

        var summary = service.Analyze(plan);

        Assert.Equal("plan-2", summary.PlanId);
        Assert.Equal(2, summary.TotalSlices);
        Assert.Equal(RiskLevel.High, summary.HighestRiskLevel);
        Assert.True(summary.HighestScore > 0);
        Assert.Equal(1, summary.ManualApprovalRequiredCount);
        Assert.Contains("manual approval", summary.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
