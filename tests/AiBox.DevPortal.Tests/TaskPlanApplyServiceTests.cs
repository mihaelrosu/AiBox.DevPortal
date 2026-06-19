using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskPlanApplyServiceTests
{
    [Fact]
    public async Task ApplyAsync_AppliesApprovedSlicesInOrder_AndStopsAfterFailure()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var approvalGateService = Substitute.For<IPatchApprovalGateService>();
            approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

            var patchPackageService = new PatchPackageService(environment, approvalGateService);
            var approvalService = new TaskSliceApprovalService(environment);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);
            var patchRollbackService = new PatchRollbackService(patchPackageService);
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);
            var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());
            var dependencyGraphService = new TaskPlanDependencyGraphService();
            var applyService = new TaskSliceApplyService(
                backupService,
                patchPackageService,
                rollbackService,
                applyHistoryService,
                approvalService);
            var planApplyService = new TaskPlanApplyService(applyService, approvalService, dependencyGraphService);

            var slice1 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-1",
                "slice-1",
                "Slice 1",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 1";
                    }
                }
                """);

            var slice2 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-1",
                "slice-2",
                "Slice 2",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Broken"
                    }
                }
                """);

            var slice3 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-1",
                "slice-3",
                "Slice 3",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 3";
                    }
                }
                """);

            var plan = new TaskPlan
            {
                Id = "plan-1",
                OriginalRequest = "Apply slices",
                CreatedAtUtc = DateTime.UtcNow,
                Slices = [slice1, slice2, slice3]
            };

            var result = await planApplyService.ApplyAsync(plan, projectRoot);

            Assert.False(result.Success);
            Assert.Equal(3, result.TotalSlices);
            Assert.Equal(1, result.AppliedSlices);
            Assert.Equal(slice2.Id, result.FailedSliceId);
            Assert.True(result.RollbackPerformed);
            Assert.Contains(result.AuditTrail, entry => entry.Contains("Plan apply started", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.AuditTrail, entry => entry.Contains("Plan apply finished", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(TaskSliceStatus.Applied, slice1.Status);
            Assert.Equal(TaskSliceStatus.RolledBack, slice2.Status);
            Assert.Equal(TaskSliceStatus.Verified, slice3.Status);

            var programText = await File.ReadAllTextAsync(Path.Combine(projectRoot, "SliceTarget.cs"));
            Assert.Contains("Slice 1", programText, StringComparison.Ordinal);
            Assert.DoesNotContain("Slice 3", programText, StringComparison.Ordinal);

            var history = await applyHistoryService.GetHistoryAsync(10);
            Assert.Equal(2, history.Count);
            Assert.True(history[0].BuildSucceeded || history[1].BuildSucceeded);
            Assert.Contains(history, item => item.SliceId == slice1.Id);
            Assert.Contains(history, item => item.SliceId == slice2.Id);
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
    public async Task ApplyAsync_AppliesAllApprovedSlices_WhenBuildsSucceed()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var approvalGateService = Substitute.For<IPatchApprovalGateService>();
            approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

            var patchPackageService = new PatchPackageService(environment, approvalGateService);
            var approvalService = new TaskSliceApprovalService(environment);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);
            var patchRollbackService = new PatchRollbackService(patchPackageService);
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);
            var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());
            var dependencyGraphService = new TaskPlanDependencyGraphService();
            var applyService = new TaskSliceApplyService(
                backupService,
                patchPackageService,
                rollbackService,
                applyHistoryService,
                approvalService);
            var planApplyService = new TaskPlanApplyService(applyService, approvalService, dependencyGraphService);

            var slice1 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-2",
                "slice-1",
                "Slice 1",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 1";
                    }
                }
                """);

            var slice2 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-2",
                "slice-2",
                "Slice 2",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 2";
                    }
                }
                """);

            var plan = new TaskPlan
            {
                Id = "plan-2",
                OriginalRequest = "Apply slices",
                CreatedAtUtc = DateTime.UtcNow,
                Slices = [slice1, slice2]
            };

            var result = await planApplyService.ApplyAsync(plan, projectRoot);

            Assert.True(result.Success, $"{result.Summary}{Environment.NewLine}{result.SliceResults.FirstOrDefault()?.BuildVerificationResult?.Output}");
            Assert.Equal(2, result.TotalSlices);
            Assert.Equal(2, result.AppliedSlices);
            Assert.Null(result.FailedSliceId);
            Assert.False(result.RollbackPerformed);

            var programText = await File.ReadAllTextAsync(Path.Combine(projectRoot, "SliceTarget.cs"));
            Assert.Contains("Slice 2", programText, StringComparison.Ordinal);

            var history = await applyHistoryService.GetHistoryAsync(10);
            Assert.Equal(2, history.Count);
            Assert.All(history, item => Assert.True(item.BuildSucceeded));
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
    public async Task ApplyAsync_AppliesSlicesInDependencyGraphOrder()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var approvalGateService = Substitute.For<IPatchApprovalGateService>();
            approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

            var patchPackageService = new PatchPackageService(environment, approvalGateService);
            var approvalService = new TaskSliceApprovalService(environment);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);
            var patchRollbackService = new PatchRollbackService(patchPackageService);
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);
            var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());
            var dependencyGraphService = new TaskPlanDependencyGraphService();
            var applyService = new TaskSliceApplyService(
                backupService,
                patchPackageService,
                rollbackService,
                applyHistoryService,
                approvalService);
            var planApplyService = new TaskPlanApplyService(applyService, approvalService, dependencyGraphService);

            var sliceA = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-3",
                "slice-a",
                "Slice A",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice A";
                    }
                }
                """);

            var sliceB = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-3",
                "slice-b",
                "Slice B",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice B";
                    }
                }
                """,
                [sliceA.Id]);

            var plan = new TaskPlan
            {
                Id = "plan-3",
                OriginalRequest = "Apply slices",
                CreatedAtUtc = DateTime.UtcNow,
                Slices = [sliceB, sliceA]
            };

            var result = await planApplyService.ApplyAsync(plan, projectRoot);

            Assert.True(result.Success, $"{result.Summary}{Environment.NewLine}{result.SliceResults.FirstOrDefault()?.BuildVerificationResult?.Output}");
            Assert.Equal(2, result.TotalSlices);
            Assert.Equal(2, result.AppliedSlices);
            Assert.Equal("slice-a", result.OrderedSliceIds[0], StringComparer.OrdinalIgnoreCase);
            Assert.Equal("slice-b", result.OrderedSliceIds[1], StringComparer.OrdinalIgnoreCase);

            var sliceAAuditIndex = result.AuditTrail.ToList().FindIndex(entry => entry.Contains("Applying slice 1/2: Slice A", StringComparison.OrdinalIgnoreCase));
            var sliceBAuditIndex = result.AuditTrail.ToList().FindIndex(entry => entry.Contains("Applying slice 2/2: Slice B", StringComparison.OrdinalIgnoreCase));

            Assert.True(sliceAAuditIndex >= 0);
            Assert.True(sliceBAuditIndex > sliceAAuditIndex);

            var programText = await File.ReadAllTextAsync(Path.Combine(projectRoot, "SliceTarget.cs"));
            Assert.Contains("Slice B", programText, StringComparison.Ordinal);
            Assert.DoesNotContain("Slice A", programText, StringComparison.Ordinal);

            var history = await applyHistoryService.GetHistoryAsync(10);
            Assert.Equal(2, history.Count);
            Assert.Equal(sliceB.Id, history[0].SliceId);
            Assert.Equal(sliceA.Id, history[1].SliceId);
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
    public async Task ApplyAsync_RejectsInvalidDependencyGraphWithoutApplyingSlices()
    {
        var projectRoot = CreateTempProjectRoot();

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var approvalGateService = Substitute.For<IPatchApprovalGateService>();
            approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

            var patchPackageService = new PatchPackageService(environment, approvalGateService);
            var approvalService = new TaskSliceApprovalService(environment);
            var applyHistoryService = new TaskSliceApplyHistoryService(environment);
            var patchRollbackService = new PatchRollbackService(patchPackageService);
            var rollbackService = new TaskSliceRollbackService(patchRollbackService);
            var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());
            var dependencyGraphService = new TaskPlanDependencyGraphService();
            var applyService = new TaskSliceApplyService(
                backupService,
                patchPackageService,
                rollbackService,
                applyHistoryService,
                approvalService);
            var planApplyService = new TaskPlanApplyService(applyService, approvalService, dependencyGraphService);

            var slice1 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-4",
                "slice-1",
                "Slice 1",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 1";
                    }
                }
                """);

            var slice2 = await CreateSliceAsync(
                projectRoot,
                patchPackageService,
                approvalService,
                "plan-4",
                "slice-2",
                "Slice 2",
                """
                using System;

                namespace TempProject;

                public static class SliceTarget
                {
                    public static string GetMessage()
                    {
                        return "Slice 2";
                    }
                }
                """,
                ["missing-slice"]);

            var plan = new TaskPlan
            {
                Id = "plan-4",
                OriginalRequest = "Apply slices",
                CreatedAtUtc = DateTime.UtcNow,
                Slices = [slice1, slice2]
            };

            var result = await planApplyService.ApplyAsync(plan, projectRoot);

            Assert.False(result.Success);
            Assert.Empty(result.SliceResults);
            Assert.Equal(0, result.AppliedSlices);
            Assert.Equal(2, result.TotalSlices);
            Assert.Contains(result.ValidationErrors, error => error.Contains("non-existent ID", StringComparison.OrdinalIgnoreCase));

            var programText = await File.ReadAllTextAsync(Path.Combine(projectRoot, "SliceTarget.cs"));
            Assert.Contains("Initial", programText, StringComparison.Ordinal);
            Assert.DoesNotContain("Slice 1", programText, StringComparison.Ordinal);
            Assert.DoesNotContain("Slice 2", programText, StringComparison.Ordinal);

            var history = await applyHistoryService.GetHistoryAsync(10);
            Assert.Empty(history);
            Assert.Equal(TaskSliceStatus.Verified, slice1.Status);
            Assert.Equal(TaskSliceStatus.Verified, slice2.Status);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static async Task<TaskPlanSlice> CreateSliceAsync(
        string projectRoot,
        PatchPackageService patchPackageService,
        TaskSliceApprovalService approvalService,
        string planId,
        string sliceId,
        string title,
        string programContent,
        IReadOnlyList<string>? dependsOnSliceIds = null)
    {
        var package = await patchPackageService.CreateFromChangesAsync(
            projectRoot,
            $"Apply {title}",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "SliceTarget.cs",
                    OldContent = string.Empty,
                    NewContent = programContent
                }
            ]);

        var slice = new TaskPlanSlice
        {
            Id = sliceId,
            PlanId = planId,
            Title = title,
            Goal = title,
            Description = title,
            PatchPackageId = package.Id,
            Status = TaskSliceStatus.Verified,
            DependsOnSliceIds = dependsOnSliceIds?.ToArray() ?? []
        };

        await approvalService.ApproveAsync(slice, "tester");
        return slice;
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"task-plan-apply-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        File.WriteAllText(
            Path.Combine(root, "TaskPlanApplyServiceTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="SliceTarget.cs" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(
            Path.Combine(root, "SliceTarget.cs"),
            """
            using System;

            namespace TempProject;

            public static class SliceTarget
            {
                public static string GetMessage()
                {
                    return "Initial";
                }
            }
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
