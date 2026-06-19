using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskDecompositionServiceTests
{
    [Fact]
    public void BuildPlan_PopulatesRiskFieldsOnGeneratedSlice()
    {
        var service = new TaskDecompositionService(new RiskAnalysisService());

        var plan = service.BuildPlan("Add a feature");

        var slice = Assert.Single(plan.Slices);
        Assert.Equal(RiskLevel.Low, slice.RiskLevel);
        Assert.Equal(0, slice.RiskScore);
        Assert.False(string.IsNullOrWhiteSpace(slice.RiskSummary));
    }
}
