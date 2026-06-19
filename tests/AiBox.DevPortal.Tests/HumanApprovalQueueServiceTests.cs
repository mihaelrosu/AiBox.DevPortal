using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class HumanApprovalQueueServiceTests
{
    [Fact]
    public async Task CreateAsync_WritesPendingApprovalRequest()
    {
        await using var context = CreateContext();

        var request = await context.Service.CreateAsync(
            "run-1",
            "Human approval test",
            RiskLevel.High,
            "Approval is required.",
            "tester");

        Assert.Equal("run-1", request.RunId);
        Assert.Equal(HumanApprovalRequestStatus.Pending, request.Status);
        Assert.True(File.Exists(Path.Combine(context.Root, "Data", "human-approval-queue.json")));
        Assert.Single(await context.Service.GetPendingAsync(10));
        Assert.Single(await context.TimelineService.GetByRunAsync("run-1"));
    }

    [Fact]
    public async Task DecideAsync_ApprovesRequest()
    {
        await using var context = CreateContext();

        var request = await context.Service.CreateAsync("run-2", "Approve test", RiskLevel.High, "Needs approval", "tester");
        var approved = await context.Service.DecideAsync(request.Id, HumanApprovalDecision.Approved, "approver", "Approved");

        Assert.Equal(HumanApprovalRequestStatus.Approved, approved.Status);
        Assert.NotNull(approved.DecisionAtUtc);
        Assert.Equal("approver", approved.DecisionBy);
        Assert.Equal("Approved", approved.Notes);
        Assert.Empty(await context.Service.GetPendingAsync(10));

        var events = await context.TimelineService.GetByRunAsync("run-2");
        var eventItem = Assert.Single(events, item => item.EventType == AgentOrchestrationTimelineEventType.ApprovalGranted);
        Assert.Equal(AgentOrchestrationTimelineSeverity.Info, eventItem.Severity);
    }

    [Fact]
    public async Task DecideAsync_RejectsRequest()
    {
        await using var context = CreateContext();

        var request = await context.Service.CreateAsync("run-3", "Reject test", RiskLevel.High, "Needs approval", "tester");
        var rejected = await context.Service.DecideAsync(request.Id, HumanApprovalDecision.Rejected, "approver", "Rejected");

        Assert.Equal(HumanApprovalRequestStatus.Rejected, rejected.Status);
        Assert.NotNull(rejected.DecisionAtUtc);
        Assert.Equal("approver", rejected.DecisionBy);
        Assert.Equal("Rejected", rejected.Notes);
        Assert.Empty(await context.Service.GetPendingAsync(10));

        var events = await context.TimelineService.GetByRunAsync("run-3");
        var eventItem = Assert.Single(events, item => item.EventType == AgentOrchestrationTimelineEventType.ApprovalRejected);
        Assert.Equal(AgentOrchestrationTimelineSeverity.Error, eventItem.Severity);
    }

    private static Context CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"human-approval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Data"));

        var environment = new TestWebHostEnvironment(root);
        var timelineService = new AgentOrchestrationTimelineService(environment);
        var service = new HumanApprovalQueueService(environment, timelineService);
        return new Context(root, service, timelineService);
    }

    private sealed record Context(
        string Root,
        HumanApprovalQueueService Service,
        AgentOrchestrationTimelineService TimelineService) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
