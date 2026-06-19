using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceApprovalServiceTests
{
    [Fact]
    public async Task ApproveAndRejectAsync_UpdatesApprovalState()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var service = new TaskSliceApprovalService(environment);
            var slice = CreateSlice();

            var pending = await service.GetOrCreateAsync(slice);
            Assert.Equal(TaskSliceApprovalStatus.PendingApproval, pending.Status);

            var approved = await service.ApproveAsync(slice, "tester");
            Assert.Equal(TaskSliceApprovalStatus.Approved, approved.Status);
            Assert.Equal("tester", approved.ApprovedBy);
            Assert.NotNull(approved.ApprovedAtUtc);

            var rejected = await service.RejectAsync(slice, "tester");
            Assert.Equal(TaskSliceApprovalStatus.Rejected, rejected.Status);
            Assert.Equal("tester", rejected.RejectedBy);
            Assert.NotNull(rejected.RejectedAtUtc);
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
    public async Task TaskSliceApplyService_RejectsPendingApproval()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var approvalService = new TaskSliceApprovalService(environment);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);

            var patchBackupService = Substitute.For<IPatchBackupService>();
            var patchPackageService = Substitute.For<IPatchPackageService>();
            var rollbackService = new TaskSliceRollbackService(Substitute.For<IPatchRollbackService>());

            var slice = CreateSlice(status: TaskSliceStatus.Verified);
            patchPackageService.GetByIdAsync(slice.PatchPackageId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<PatchPackage?>(new PatchPackage
                {
                    Id = slice.PatchPackageId,
                    ProjectPath = projectRoot,
                    Status = PatchPackageStatus.Approved,
                    FileChanges = []
                }));

            var applyService = new TaskSliceApplyService(
                patchBackupService,
                patchPackageService,
                new TaskSliceRollbackService(Substitute.For<IPatchRollbackService>()),
                applyHistoryService,
                approvalService);

            var result = await applyService.ApplyAsync(projectRoot, slice);

            Assert.False(result.Success);
            Assert.Contains("approved", result.Message, StringComparison.OrdinalIgnoreCase);
            await patchBackupService.DidNotReceiveWithAnyArgs().CreateBackupAsync(default!, default!, default);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static TaskPlanSlice CreateSlice(TaskSliceStatus status = TaskSliceStatus.Verified)
    {
        return new TaskPlanSlice
        {
            PlanId = "plan-1",
            Title = "Approval slice",
            Goal = "Keep the project safe",
            Description = "Keep the project safe",
            PatchPackageId = "patch-1",
            Status = status
        };
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"task-slice-approval-{Guid.NewGuid():N}");
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
