using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceApplyAuditServiceTests
{
    [Fact]
    public async Task SuccessfulApply_WritesAuditEntry()
    {
        await using var context = await CreateContextAsync("SuccessfulApply_WritesAuditEntry");

        var slice = await CreateSliceAsync(context, "low", RiskLevel.Low, """
            Console.WriteLine("Updated low");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice);

        Assert.True(result.Success);

        var entry = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.True(entry.Success);
        Assert.False(entry.Blocked);
        Assert.Equal(slice.PlanId, entry.PlanId);
        Assert.Equal(slice.Id, entry.SliceId);
        Assert.Equal(slice.Title, entry.SliceTitle);
        Assert.Equal(RiskLevel.Low, entry.RiskLevel);
        Assert.Equal(slice.RiskScore, entry.RiskScore);
        Assert.False(entry.ApprovedHighRiskApply);
        Assert.Single(entry.ChangedFiles);
        Assert.Contains("Program.cs", entry.ChangedFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HighRiskBlockedApply_WritesBlockedAuditEntry()
    {
        await using var context = await CreateContextAsync("HighRiskBlockedApply_WritesBlockedAuditEntry");

        var slice = await CreateSliceAsync(context, "high", RiskLevel.High, """
            Console.WriteLine("Updated high");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice, highRiskApproved: false);

        Assert.False(result.Success);

        var entry = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.False(entry.Success);
        Assert.True(entry.Blocked);
        Assert.Equal(RiskLevel.High, entry.RiskLevel);
        Assert.False(entry.ApprovedHighRiskApply);
        Assert.Empty(entry.ChangedFiles);
        Assert.Contains("manual approval", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CriticalBlockedApply_WritesBlockedAuditEntry()
    {
        await using var context = await CreateContextAsync("CriticalBlockedApply_WritesBlockedAuditEntry");

        var slice = await CreateSliceAsync(context, "critical", RiskLevel.Critical, """
            Console.WriteLine("Updated critical");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice, highRiskApproved: true);

        Assert.False(result.Success);

        var entry = Assert.Single(await context.AuditService.GetLatestAsync(10));
        Assert.False(entry.Success);
        Assert.True(entry.Blocked);
        Assert.Equal(RiskLevel.Critical, entry.RiskLevel);
        Assert.True(entry.ApprovedHighRiskApply);
        Assert.Empty(entry.ChangedFiles);
        Assert.Contains("cannot be applied", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<TaskPlanSlice> CreateSliceAsync(
        TestContextBundle context,
        string sliceId,
        RiskLevel riskLevel,
        string newProgramText)
    {
        var package = await context.PatchPackageService.CreateFromChangesAsync(
            context.ProjectRoot,
            $"Apply {sliceId}",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "Program.cs",
                    OldContent = """
                        Console.WriteLine("Initial");
                        """,
                    NewContent = newProgramText
                }
            ]);

        await context.PatchPackageService.ApproveAsync(package.Id);

        var slice = new TaskPlanSlice
        {
            Id = sliceId,
            PlanId = "plan-1",
            Title = sliceId,
            Goal = sliceId,
            Description = sliceId,
            PatchPackageId = package.Id,
            Status = TaskSliceStatus.Verified,
            RiskLevel = riskLevel
        };

        await context.ApprovalService.ApproveAsync(slice, "tester");
        return slice;
    }

    private static Task<TestContextBundle> CreateContextAsync(string testName)
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"task-slice-audit-{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);

        File.WriteAllText(
            Path.Combine(projectRoot, "TaskSliceApplyAuditServiceTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Program.cs" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(
            Path.Combine(projectRoot, "Program.cs"),
            """
            Console.WriteLine("Initial");
            """);

        var environment = new TestWebHostEnvironment(projectRoot);
        var approvalGateService = Substitute.For<IPatchApprovalGateService>();
        approvalGateService.EvaluateAsync(Arg.Any<PatchPackage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PatchApprovalGateResult>>([]));

        var patchPackageService = new PatchPackageService(environment, approvalGateService);
        var approvalService = new TaskSliceApprovalService(environment);
        var applyHistoryService = new TaskSliceApplyHistoryService(environment);
        var auditService = new TaskSliceApplyAuditService(environment);
        var patchRollbackService = Substitute.For<IPatchRollbackService>();
        var rollbackService = new TaskSliceRollbackService(patchRollbackService);
        var backupService = new PatchBackupService(environment, Substitute.For<ILogger<PatchBackupService>>());
        var applyService = new TaskSliceApplyService(
            backupService,
            patchPackageService,
            patchRollbackService,
            rollbackService,
            applyHistoryService,
            approvalService,
            auditService);

        return Task.FromResult(new TestContextBundle(projectRoot, environment, patchPackageService, approvalService, applyHistoryService, auditService, applyService));
    }

    private sealed record TestContextBundle(
        string ProjectRoot,
        IWebHostEnvironment Environment,
        PatchPackageService PatchPackageService,
        TaskSliceApprovalService ApprovalService,
        TaskSliceApplyHistoryService ApplyHistoryService,
        TaskSliceApplyAuditService AuditService,
        TaskSliceApplyService ApplyService) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(ProjectRoot))
            {
                Directory.Delete(ProjectRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
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
