using AiBox.DevPortal.Components.Coder;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Radzen;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceRollbackServiceTests
{
    [Fact]
    public async Task RollbackSliceAsync_CompleteFlow_RollsBackAppliedSlice()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var executionService = new TaskSliceExecutionService(environment);
            var verificationService = new TaskSliceVerificationService(environment);
            var slice = new TaskPlanSlice
            {
                PlanId = "plan-1",
                Title = "Safe slice",
                Goal = "Keep the project building",
                Description = "Keep the project building",
                PatchPackageId = "patch-1",
                Status = TaskSliceStatus.Pending
            };

            var patchBackupService = Substitute.For<IPatchBackupService>();
            patchBackupService.CreateBackupAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PatchBackupResult
                {
                    BackupFolderPath = "Data/patch-backups/test",
                    BackedUpFilesCount = 1
                }));

            var patchPackageService = Substitute.For<IPatchPackageService>();
            patchPackageService.GetByIdAsync(slice.PatchPackageId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<PatchPackage?>(new PatchPackage
                {
                    Id = slice.PatchPackageId,
                    ProjectPath = projectRoot,
                    Status = PatchPackageStatus.Approved,
                    FileChanges =
                    [
                        new PatchFileChange
                        {
                            RelativePath = "Program.cs",
                            NewContent = """
            Console.WriteLine("Hello, world!");
            """
                        }
                    ]
                }));
            patchPackageService.SaveAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(callInfo.Arg<PatchPackage>()));

            var patchRollbackService = Substitute.For<IPatchRollbackService>();
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);
            var approvalService = new TaskSliceApprovalService(environment);
            var auditService = new TaskSliceApplyAuditService(environment);
            var applyService = new TaskSliceApplyService(
                patchBackupService,
                patchPackageService,
                patchRollbackService,
                rollbackService,
                applyHistoryService,
                approvalService,
                auditService);

            var generateResult = await executionService.ExecuteSliceAsync(
                new TaskSliceExecutionRequest
                {
                    PlanId = slice.PlanId,
                    SliceId = slice.Id,
                    SliceTitle = slice.Title,
                    RequestedAction = "GeneratePatch",
                    RequestedAt = DateTime.UtcNow,
                    RequestedBy = "test"
                },
                slice);

            Assert.True(generateResult.Success);
            Assert.Equal(TaskSliceStatus.Previewed, slice.Status);

            var verificationResult = await verificationService.VerifySliceAsync(slice);
            Assert.True(verificationResult.Success);
            Assert.Equal(TaskSliceStatus.Verified, slice.Status);

            await approvalService.ApproveAsync(slice, "tester");

            var applyResult = await applyService.ApplyAsync(projectRoot, slice);
            Assert.True(applyResult.Success);
            Assert.Equal(TaskSliceStatus.Applied, slice.Status);
            Assert.Equal(1, applyResult.AppliedFilesCount);
            Assert.NotNull(applyResult.BuildVerificationResult);
            Assert.True(applyResult.BuildVerificationResult!.Success);

            var rolledBackAt = DateTimeOffset.UtcNow;
            patchRollbackService.RollbackAsync(slice.PatchPackageId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PatchPackage
                {
                    Id = slice.PatchPackageId,
                    Status = PatchPackageStatus.RolledBack,
                    RolledBackAt = rolledBackAt,
                    BackupFolder = "Data/patch-backups/test"
                }));

            var rollbackResult = await rollbackService.RollbackSliceAsync(slice);

            Assert.True(rollbackResult.Success);
            Assert.Equal(TaskSliceStatus.RolledBack, slice.Status);
            Assert.NotNull(slice.RolledBackAt);
            Assert.Equal(rolledBackAt.UtcDateTime, slice.RolledBackAt!.Value, TimeSpan.FromSeconds(5));
            Assert.Equal(slice.PatchPackageId, rollbackResult.PatchPackageId);
            await patchRollbackService.Received(1).RollbackAsync(slice.PatchPackageId, Arg.Any<CancellationToken>());
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
    public void TaskPlanPreviewPanel_RendersRollbackButtonOnlyForAppliedSlices()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var appliedPlan = new TaskPlan
        {
            OriginalRequest = "Test",
            Slices =
            [
                new TaskPlanSlice { Title = "Applied slice", Status = TaskSliceStatus.Applied, PatchPackageId = "patch-1" }
            ]
        };

        var appliedCut = ctx.RenderComponent<TaskPlanPreviewPanel>(parameters => parameters
            .Add(component => component.Plan, appliedPlan)
            .Add(component => component.IsGeneratingPatch, false)
            .Add(component => component.IsVerifyingSlice, false)
            .Add(component => component.IsRollingBack, false));

        Assert.Contains("Rollback", appliedCut.Markup, StringComparison.Ordinal);

        var nonAppliedPlan = new TaskPlan
        {
            OriginalRequest = "Test",
            Slices =
            [
                new TaskPlanSlice { Title = "Pending slice", Status = TaskSliceStatus.Pending },
                new TaskPlanSlice { Title = "Verified slice", Status = TaskSliceStatus.Verified }
            ]
        };

        var nonAppliedCut = ctx.RenderComponent<TaskPlanPreviewPanel>(parameters => parameters
            .Add(component => component.Plan, nonAppliedPlan)
            .Add(component => component.IsGeneratingPatch, false)
            .Add(component => component.IsVerifyingSlice, false)
            .Add(component => component.IsRollingBack, false));

        Assert.DoesNotContain("Rollback", nonAppliedCut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskPlanPreviewPanel_RendersApprovalControlsForVerifiedSlices()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var verifiedPlan = new TaskPlan
        {
            OriginalRequest = "Test",
            Slices =
            [
                new TaskPlanSlice { Title = "Verified slice", Status = TaskSliceStatus.Verified, PatchPackageId = "patch-1" }
            ]
        };

        var cut = ctx.RenderComponent<TaskPlanPreviewPanel>(parameters => parameters
            .Add(component => component.Plan, verifiedPlan)
            .Add(component => component.IsGeneratingPatch, false)
            .Add(component => component.IsVerifyingSlice, false)
            .Add(component => component.IsRollingBack, false)
            .Add(component => component.IsApplyingSlice, false)
            .Add(component => component.IsApprovalBusy, false));

        Assert.Contains("Approval", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(TaskSliceApprovalStatus.PendingApproval.ToString(), cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Approve", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Reject", cut.Markup, StringComparison.Ordinal);

        var applyButton = cut.FindAll("button")
            .First(button => button.TextContent.Contains("Apply", StringComparison.Ordinal));

        Assert.True(applyButton.HasAttribute("disabled"));
    }

    [Fact]
    public void TaskPlanPreviewPanel_RendersRiskAnalysisForSlices()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var plan = new TaskPlan
        {
            OriginalRequest = "Test",
            Slices =
            [
                new TaskPlanSlice
                {
                    Title = "Risky slice",
                    Status = TaskSliceStatus.Verified,
                    PatchPackageId = "patch-1",
                    TargetFiles = ["Program.cs", "Pages/Home.razor"],
                    VerificationCommands = [],
                    Notes = "Urgent hotfix"
                }
            ]
        };

        var analysisService = new TaskSliceRiskAnalysisService();
        var analysis = analysisService.Analyze(plan.Slices[0]);

        var cut = ctx.RenderComponent<TaskPlanPreviewPanel>(parameters => parameters
            .Add(component => component.Plan, plan)
            .Add(component => component.IsGeneratingPatch, false)
            .Add(component => component.IsVerifyingSlice, false)
            .Add(component => component.IsRollingBack, false)
            .Add(component => component.IsApplyingSlice, false)
            .Add(component => component.IsApprovalBusy, false)
            .Add(component => component.SliceRiskAnalyses, new Dictionary<string, TaskSliceRiskAnalysis>
            {
                [analysis.SliceId] = analysis
            }));

        Assert.Contains("Risk", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(TaskSliceRiskLevel.High.ToString(), cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Application startup file.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("No verification commands were provided.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskPlanPreviewPanel_RendersVerificationLoopResults()
    {
        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddRadzenComponents();

        var plan = new TaskPlan
        {
            OriginalRequest = "Test",
            Slices =
            [
                new TaskPlanSlice
                {
                    Id = "slice-1",
                    Title = "Loop slice",
                    Status = TaskSliceStatus.Previewed,
                    PatchPackageId = "patch-1"
                }
            ]
        };

        var loopResult = new TaskSliceVerificationLoopResult
        {
            Success = false,
            Attempts = 2,
            FinalMessage = "Verification failed after 2 attempts.",
            VerificationResults =
            [
                new TaskSliceExecutionResult
                {
                    SliceId = "slice-1",
                    SliceTitle = "Loop slice",
                    Summary = "Attempt one failed.",
                    Errors = ["First error"],
                    ExecutedAt = DateTime.UtcNow
                },
                new TaskSliceExecutionResult
                {
                    SliceId = "slice-1",
                    SliceTitle = "Loop slice",
                    Summary = "Attempt two failed.",
                    Errors = ["Second error"],
                    ExecutedAt = DateTime.UtcNow
                }
            ]
        };

        var cut = ctx.RenderComponent<TaskPlanPreviewPanel>(parameters => parameters
            .Add(component => component.Plan, plan)
            .Add(component => component.IsGeneratingPatch, false)
            .Add(component => component.IsVerifyingSlice, false)
            .Add(component => component.IsRollingBack, false)
            .Add(component => component.IsApplyingSlice, false)
            .Add(component => component.IsApprovalBusy, false)
            .Add(component => component.SliceVerificationLoopResults, new Dictionary<string, TaskSliceVerificationLoopResult>
            {
                [loopResult.VerificationResults[0].SliceId] = loopResult
            }));

        Assert.Contains("Verification Loop", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Attempt 1/3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Attempt 2/3", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Final result", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(loopResult.FinalMessage, cut.Markup, StringComparison.Ordinal);
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"task-slice-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        File.WriteAllText(
            Path.Combine(root, "TaskSliceRollbackServiceTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(
            Path.Combine(root, "Program.cs"),
            """
            Console.WriteLine("Hello, world!");
            """);

        return root;
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
