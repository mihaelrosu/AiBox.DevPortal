using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentDashboardMetricsServiceTests
{
    [Fact]
    public async Task GetMetricsAsync_AggregatesApplyHistory()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var historyService = new TaskSliceApplyHistoryService(environment);
            var metricsService = new AgentDashboardMetricsService(historyService);

            await historyService.AddAsync(new TaskSliceApplyHistoryItem
            {
                PlanId = "plan-1",
                SliceId = "slice-1",
                SliceTitle = "Slice one",
                PatchPackageId = "patch-1",
                AppliedAtUtc = new DateTime(2026, 6, 18, 10, 0, 0, DateTimeKind.Utc),
                BuildSucceeded = true,
                RolledBack = false,
                RollbackSucceeded = false,
                ResultMessage = "Applied successfully"
            });

            await historyService.AddAsync(new TaskSliceApplyHistoryItem
            {
                PlanId = "plan-1",
                SliceId = "slice-2",
                SliceTitle = "Slice two",
                PatchPackageId = "patch-2",
                AppliedAtUtc = new DateTime(2026, 6, 18, 11, 0, 0, DateTimeKind.Utc),
                BuildSucceeded = false,
                RolledBack = true,
                RollbackSucceeded = true,
                ResultMessage = "Build failed. Rollback completed successfully."
            });

            await historyService.AddAsync(new TaskSliceApplyHistoryItem
            {
                PlanId = "plan-2",
                SliceId = "slice-3",
                SliceTitle = "Slice three",
                PatchPackageId = "patch-3",
                AppliedAtUtc = new DateTime(2026, 6, 18, 12, 0, 0, DateTimeKind.Utc),
                BuildSucceeded = false,
                RolledBack = true,
                RollbackSucceeded = false,
                ResultMessage = "Build failed. Rollback failed."
            });

            var metrics = await metricsService.GetMetricsAsync();

            Assert.Equal(3, metrics.TotalAttempts);
            Assert.Equal(1, metrics.SuccessfulApplies);
            Assert.Equal(2, metrics.FailedApplies);
            Assert.Equal(1, metrics.BuildSucceededAttempts);
            Assert.Equal(2, metrics.BuildFailedAttempts);
            Assert.Equal(2, metrics.RolledBackAttempts);
            Assert.Equal(1, metrics.RollbackSucceededAttempts);
            Assert.Equal(1, metrics.RollbackFailedAttempts);
            Assert.Equal(33.3, metrics.BuildSuccessRate, 1);
            Assert.Equal(50.0, metrics.RollbackSuccessRate, 1);
            Assert.Equal(3, metrics.RecentAttempts.Count);
            Assert.Equal("Slice three", metrics.RecentAttempts[0].SliceTitle);
            Assert.Contains(metrics.TopFailureMessages, item => item.Contains("Rollback failed", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-dashboard-metrics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AiBox.DevPortal.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string EnvironmentName { get; set; } = Environments.Development;
    }
}
