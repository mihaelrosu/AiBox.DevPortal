using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class TaskSliceVerificationLoopServiceTests
{
    [Fact]
    public async Task VerifyAsync_SucceedsOnFirstAttempt_WhenBuildPasses()
    {
        var projectRoot = CreateTempProjectRoot(valid: true);

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var verificationService = new TaskSliceVerificationService(environment);
            var loopService = new TaskSliceVerificationLoopService(verificationService, new TaskSliceRiskAnalysisService());

            var slice = CreatePreviewedSlice();

            var result = await loopService.VerifyAsync(slice);

            Assert.True(result.Success);
            Assert.Equal(1, result.Attempts);
            Assert.Single(result.VerificationResults);
            Assert.True(result.VerificationResults[0].Success);
            Assert.Contains("Verification succeeded on attempt 1/3.", result.FinalMessage, StringComparison.Ordinal);
            Assert.Equal(TaskSliceStatus.Verified, slice.Status);
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
    public async Task VerifyAsync_CreatesRemediationSliceAndRetries_WhenBuildFails()
    {
        var projectRoot = CreateTempProjectRoot(valid: false);

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var verificationService = new TaskSliceVerificationService(environment);
            var loopService = new TaskSliceVerificationLoopService(verificationService, new TaskSliceRiskAnalysisService());

            var slice = CreatePreviewedSlice();

            var result = await loopService.VerifyAsync(slice);

            Assert.False(result.Success);
            Assert.Equal(TaskSliceVerificationLoopService.MaxAttempts, result.Attempts);
            Assert.Equal(TaskSliceVerificationLoopService.MaxAttempts, result.VerificationResults.Count);
            Assert.NotNull(result.GeneratedFixSlice);
            Assert.Equal(TaskSliceStatus.Failed, result.GeneratedFixSlice!.Status);
            Assert.Contains("Verification failed after 3 attempts.", result.FinalMessage, StringComparison.Ordinal);
            Assert.Equal(TaskSliceStatus.Failed, slice.Status);
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
    public async Task VerifyAsync_StopsOnCriticalRisk_WithoutRunningBuild()
    {
        var projectRoot = CreateTempProjectRoot(valid: true);

        try
        {
            var environment = new TestWebHostEnvironment(projectRoot);
            var verificationService = new TaskSliceVerificationService(environment);
            var loopService = new TaskSliceVerificationLoopService(verificationService, new TaskSliceRiskAnalysisService());

            var slice = new TaskPlanSlice
            {
                PlanId = "plan-1",
                Title = "Critical hotfix",
                Goal = "Urgent critical hotfix for startup, settings, and auth",
                Description = "Urgent critical hotfix for startup, settings, and auth",
                Status = TaskSliceStatus.Previewed,
                TargetFiles =
                [
                    "Program.cs",
                    "appsettings.json",
                    "Services/Auth/AuthService.cs"
                ],
                VerificationCommands = [],
                Notes = "Urgent critical hotfix"
            };

            var result = await loopService.VerifyAsync(slice);

            Assert.False(result.Success);
            Assert.Equal(0, result.Attempts);
            Assert.Empty(result.VerificationResults);
            Assert.Null(result.GeneratedFixSlice);
            Assert.Contains("critical risk", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(TaskSliceStatus.Previewed, slice.Status);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static TaskPlanSlice CreatePreviewedSlice()
    {
        return new TaskPlanSlice
        {
            PlanId = "plan-1",
            Title = "Previewed slice",
            Goal = "Keep the project building",
            Description = "Keep the project building",
            Status = TaskSliceStatus.Previewed,
            TargetFiles = ["Program.cs"],
            VerificationCommands = ["dotnet build"]
        };
    }

    private static string CreateTempProjectRoot(bool valid)
    {
        var root = Path.Combine(Path.GetTempPath(), $"verification-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "TestApp.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""");

        File.WriteAllText(Path.Combine(root, "Program.cs"), valid
            ? """
using System;

Console.WriteLine("Hello from verification loop tests");
"""
            : """
using System;

Console.WriteLine(MissingSymbol);
""");

        return root;
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
