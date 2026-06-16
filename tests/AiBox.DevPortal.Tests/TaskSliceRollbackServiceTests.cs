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
            var patchApplyService = Substitute.For<IPatchApplyService>();
            var patchRollbackService = Substitute.For<IPatchRollbackService>();
            var applyService = new TaskSliceApplyService(patchApplyService);
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);

            var slice = new TaskPlanSlice
            {
                PlanId = "plan-1",
                Title = "Safe slice",
                Goal = "Keep the project building",
                Description = "Keep the project building",
                PatchPackageId = "patch-1",
                Status = TaskSliceStatus.Pending
            };

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

            var appliedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            patchApplyService.ApplyAsync(slice.PatchPackageId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PatchPackage
                {
                    Id = slice.PatchPackageId,
                    Status = PatchPackageStatus.Applied,
                    AppliedAt = appliedAt,
                    BackupFolder = "Data/patch-backups/test"
                }));

            var applyResult = await applyService.ApplySliceAsync(slice);
            Assert.True(applyResult.Success);
            Assert.Equal(TaskSliceStatus.Applied, slice.Status);
            Assert.Equal(slice.PatchPackageId, applyResult.PatchPackageId);

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
