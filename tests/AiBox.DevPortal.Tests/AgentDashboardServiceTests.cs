using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentDashboardServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_AggregatesAllSources()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var runHistoryService = new AgentRunHistoryService(environment);
            var auditService = new TaskSliceApplyAuditService(environment);
            var patchPackageService = Substitute.For<IPatchPackageService>();
            var rollbackService = new PatchRollbackService(patchPackageService, environment);
            var dashboardService = new AgentDashboardService(runHistoryService, auditService, rollbackService);

            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.Zero),
                ActionKey = "create-plan",
                Model = "qwen2.5-coder:7b",
                Success = true
            });
            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 13, 0, 0, TimeSpan.Zero),
                ActionKey = "generate-patch-preview",
                Model = "qwen2.5-coder:7b",
                Success = false
            });
            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 14, 0, 0, TimeSpan.Zero),
                ActionKey = "verify-project",
                Model = "qwen2.5-coder:7b",
                Success = true
            });
            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 14, 30, 0, TimeSpan.Zero),
                ActionKey = "verify-project",
                Model = "gpt-oss:20b",
                Success = false
            });
            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 14, 45, 0, TimeSpan.Zero),
                ActionKey = "review",
                Model = "gpt-oss:20b",
                Success = true
            });
            await runHistoryService.AddAsync(new AgentRunRecord
            {
                Timestamp = new DateTimeOffset(2026, 6, 18, 15, 0, 0, TimeSpan.Zero),
                ActionKey = "review",
                Model = "gpt-oss:20b",
                Success = false
            });

            await auditService.AddAsync(new TaskSliceApplyAuditEntry
            {
                TimestampUtc = new DateTime(2026, 6, 18, 15, 15, 0, DateTimeKind.Utc),
                PlanId = "plan-1",
                SliceId = "slice-1",
                SliceTitle = "Low slice",
                RiskLevel = RiskLevel.Low,
                Success = true,
                Blocked = false,
                Message = "Applied",
                ChangedFiles = ["Program.cs"]
            });
            await auditService.AddAsync(new TaskSliceApplyAuditEntry
            {
                TimestampUtc = new DateTime(2026, 6, 18, 16, 0, 0, DateTimeKind.Utc),
                PlanId = "plan-1",
                SliceId = "slice-2",
                SliceTitle = "High slice",
                RiskLevel = RiskLevel.High,
                Success = false,
                Blocked = true,
                Message = "Blocked",
                ChangedFiles = []
            });
            await auditService.AddAsync(new TaskSliceApplyAuditEntry
            {
                TimestampUtc = new DateTime(2026, 6, 18, 17, 0, 0, DateTimeKind.Utc),
                PlanId = "plan-2",
                SliceId = "slice-3",
                SliceTitle = "Critical slice",
                RiskLevel = RiskLevel.Critical,
                Success = false,
                Blocked = true,
                Message = "Blocked",
                ChangedFiles = []
            });

            await rollbackService.RecordAsync(new PatchRollbackEntry
            {
                TimestampUtc = new DateTime(2026, 6, 18, 18, 0, 0, DateTimeKind.Utc),
                PlanId = "plan-1",
                SliceId = "slice-2",
                BackupId = "backup-1",
                Restored = true,
                RestoreTimestampUtc = new DateTime(2026, 6, 18, 18, 5, 0, DateTimeKind.Utc),
                ChangedFiles = ["Program.cs"]
            });
            await rollbackService.RecordAsync(new PatchRollbackEntry
            {
                TimestampUtc = new DateTime(2026, 6, 18, 19, 0, 0, DateTimeKind.Utc),
                PlanId = "plan-2",
                SliceId = "slice-3",
                BackupId = "backup-2",
                Restored = false,
                RestoreTimestampUtc = new DateTime(2026, 6, 18, 19, 5, 0, DateTimeKind.Utc),
                ChangedFiles = []
            });

            var summary = await dashboardService.GetSummaryAsync();

            Assert.Equal(6, summary.TotalAgentRuns);
            Assert.Equal(3, summary.SuccessfulRuns);
            Assert.Equal(3, summary.FailedRuns);
            Assert.Equal(50.0, summary.OverallSuccessRate, 1);
            Assert.Equal(2, summary.ModelUsage.Count);
            Assert.Equal("gpt-oss:20b", summary.ModelUsage[0].ModelName);
            Assert.Equal(3, summary.ModelUsage[0].TotalRuns);
            Assert.Equal(3, summary.ModelUsage[1].TotalRuns);
            Assert.Equal(6, summary.ActionMetrics.Count);
            Assert.Collection(
                summary.ActionMetrics,
                item =>
                {
                    Assert.Equal("create-plan", item.ActionKey);
                    Assert.Equal(1, item.TotalRuns);
                    Assert.Equal(1, item.SuccessfulRuns);
                    Assert.Equal(0, item.FailedRuns);
                },
                item =>
                {
                    Assert.Equal("generate-patch-preview", item.ActionKey);
                    Assert.Equal(1, item.TotalRuns);
                    Assert.Equal(0, item.SuccessfulRuns);
                    Assert.Equal(1, item.FailedRuns);
                },
                item =>
                {
                    Assert.Equal("verify-project", item.ActionKey);
                    Assert.Equal(2, item.TotalRuns);
                    Assert.Equal(1, item.SuccessfulRuns);
                    Assert.Equal(1, item.FailedRuns);
                },
                item =>
                {
                    Assert.Equal("review", item.ActionKey);
                    Assert.Equal(2, item.TotalRuns);
                    Assert.Equal(1, item.SuccessfulRuns);
                    Assert.Equal(1, item.FailedRuns);
                },
                item =>
                {
                    Assert.Equal("apply", item.ActionKey);
                    Assert.Equal(3, item.TotalRuns);
                    Assert.Equal(1, item.SuccessfulRuns);
                    Assert.Equal(2, item.FailedRuns);
                },
                item =>
                {
                    Assert.Equal("rollback", item.ActionKey);
                    Assert.Equal(2, item.TotalRuns);
                    Assert.Equal(1, item.SuccessfulRuns);
                    Assert.Equal(1, item.FailedRuns);
                });
            Assert.Equal(3, summary.ApplyAttempts);
            Assert.Equal(1, summary.SuccessfulApplies);
            Assert.Equal(2, summary.BlockedApplies);
            Assert.InRange(summary.ApplySuccessRate, 33.2, 33.4);
            Assert.Equal(2, summary.RollbackCount);
            Assert.Equal(1, summary.LowRiskApplyAttempts);
            Assert.Equal(0, summary.MediumRiskApplyAttempts);
            Assert.Equal(1, summary.HighRiskApplyAttempts);
            Assert.Equal(1, summary.CriticalRiskApplyAttempts);
            Assert.InRange(summary.VerificationSuccessRate ?? 0, 49.9, 50.1);
            Assert.Equal("review", summary.RecentAgentRuns[0].ActionKey);
            Assert.Equal("Critical slice", summary.RecentApplyAttempts[0].SliceTitle);
            Assert.Equal("slice-3", summary.RecentRollbacks[0].SliceId);
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
    public async Task GetSummaryAsync_HandlesMissingHistoryFiles()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var runHistoryService = new AgentRunHistoryService(environment);
            var auditService = new TaskSliceApplyAuditService(environment);
            var patchPackageService = Substitute.For<IPatchPackageService>();
            var rollbackService = new PatchRollbackService(patchPackageService, environment);
            var dashboardService = new AgentDashboardService(runHistoryService, auditService, rollbackService);

            var summary = await dashboardService.GetSummaryAsync();

            Assert.Equal(0, summary.TotalAgentRuns);
            Assert.Equal(0, summary.SuccessfulRuns);
            Assert.Equal(0, summary.FailedRuns);
            Assert.Empty(summary.ModelUsage);
            Assert.Equal(6, summary.ActionMetrics.Count);
            Assert.All(summary.ActionMetrics, item =>
            {
                Assert.Equal(0, item.TotalRuns);
                Assert.Equal(0, item.SuccessfulRuns);
                Assert.Equal(0, item.FailedRuns);
            });
            Assert.Equal(0, summary.ApplyAttempts);
            Assert.Equal(0, summary.SuccessfulApplies);
            Assert.Equal(0, summary.BlockedApplies);
            Assert.Equal(0, summary.RollbackCount);
            Assert.Equal(0, summary.OverallSuccessRate, 1);
            Assert.Equal(0, summary.ApplySuccessRate, 1);
            Assert.Null(summary.VerificationSuccessRate);
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
        var root = Path.Combine(Path.GetTempPath(), $"agent-dashboard-summary-{Guid.NewGuid():N}");
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
        public string EnvironmentName { get; set; } = "Development";
    }
}
