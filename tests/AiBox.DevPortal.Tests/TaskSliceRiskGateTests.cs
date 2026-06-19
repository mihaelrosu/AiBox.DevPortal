using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceRiskGateTests
{
    [Fact]
    public async Task LowRiskSlice_ApplySucceeds()
    {
        await using var context = await CreateContextAsync("LowRiskSlice_ApplySucceeds");

        var slice = await CreateSliceAsync(
            context,
            "low",
            RiskLevel.Low,
            """
            Console.WriteLine("Updated low");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice);

        Assert.True(result.Success);
        Assert.Null(result.RiskGateMessage);
        Assert.Equal(TaskSliceStatus.Applied, slice.Status);
        Assert.Contains("Applied successfully", result.Message, StringComparison.OrdinalIgnoreCase);
        var programText = await File.ReadAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"));
        Assert.Contains("Updated low", programText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediumRiskSlice_ApplySucceedsWithWarning()
    {
        await using var context = await CreateContextAsync("MediumRiskSlice_ApplySucceedsWithWarning");

        var slice = await CreateSliceAsync(
            context,
            "medium",
            RiskLevel.Medium,
            """
            Console.WriteLine("Updated medium");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice);

        Assert.True(result.Success);
        Assert.Equal(TaskSliceStatus.Applied, slice.Status);
        Assert.Contains("Medium-risk", result.RiskGateMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var programText = await File.ReadAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"));
        Assert.Contains("Updated medium", programText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HighRiskSlice_ApplyFailsWithoutApproval()
    {
        await using var context = await CreateContextAsync("HighRiskSlice_ApplyFailsWithoutApproval");

        var slice = await CreateSliceAsync(
            context,
            "high",
            RiskLevel.High,
            """
            Console.WriteLine("Updated high");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice, highRiskApproved: false);

        Assert.False(result.Success);
        Assert.Equal(TaskSliceStatus.Verified, slice.Status);
        Assert.Contains("manual approval", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("manual approval", string.Join(Environment.NewLine, result.Errors), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HighRiskSlice_ApplySucceedsWithApproval()
    {
        await using var context = await CreateContextAsync("HighRiskSlice_ApplySucceedsWithApproval");

        var slice = await CreateSliceAsync(
            context,
            "high-approved",
            RiskLevel.High,
            """
            Console.WriteLine("Updated high approved");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice, highRiskApproved: true);

        Assert.True(result.Success);
        Assert.Equal(TaskSliceStatus.Applied, slice.Status);
        Assert.Contains("High-risk apply approved", result.RiskGateMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var programText = await File.ReadAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"));
        Assert.Contains("Updated high approved", programText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalRiskSlice_ApplyAlwaysFails()
    {
        await using var context = await CreateContextAsync("CriticalRiskSlice_ApplyAlwaysFails");

        var slice = await CreateSliceAsync(
            context,
            "critical",
            RiskLevel.Critical,
            """
            Console.WriteLine("Updated critical");
            """);

        var result = await context.ApplyService.ApplyAsync(context.ProjectRoot, slice, highRiskApproved: true);

        Assert.False(result.Success);
        Assert.Equal(TaskSliceStatus.Verified, slice.Status);
        Assert.Contains("cannot be applied", result.Message, StringComparison.OrdinalIgnoreCase);
        var programText = await File.ReadAllTextAsync(Path.Combine(context.ProjectRoot, "Program.cs"));
        Assert.DoesNotContain("Updated critical", programText, StringComparison.Ordinal);
    }

    private static async Task<TaskPlanSlice> CreateSliceAsync(
        TestContextBundle context,
        string sliceId,
        RiskLevel riskLevel,
        string newProgramText)
    {
        var initialProgramText = """
            Console.WriteLine("Initial");
            """;

        var package = await context.PatchPackageService.CreateFromChangesAsync(
            context.ProjectRoot,
            $"Apply {sliceId}",
            "test-model",
            "patch text",
            [
                new PatchFileChange
                {
                    RelativePath = "Program.cs",
                    OldContent = initialProgramText,
                    NewContent = newProgramText
                }
            ]);

        var approvedPackage = await context.PatchPackageService.ApproveAsync(package.Id);
        Assert.NotNull(approvedPackage);

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
        var projectRoot = Path.Combine(Path.GetTempPath(), $"task-slice-risk-{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectRoot);

        File.WriteAllText(
            Path.Combine(projectRoot, "TaskSliceRiskGateTests.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
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

        return Task.FromResult(new TestContextBundle(projectRoot, environment, patchPackageService, approvalService, applyHistoryService, applyService));
    }

    private sealed record TestContextBundle(
        string ProjectRoot,
        IWebHostEnvironment Environment,
        PatchPackageService PatchPackageService,
        TaskSliceApprovalService ApprovalService,
        TaskSliceApplyHistoryService ApplyHistoryService,
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
