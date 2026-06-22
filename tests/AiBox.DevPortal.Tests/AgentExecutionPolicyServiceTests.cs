using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentExecutionPolicyServiceTests
{
    [Fact]
    public async Task GetAllAsync_LoadsSeedPolicies()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            var policies = await service.GetAllAsync();

            Assert.Equal(5, policies.Count);
            Assert.Equal(["Patch Builder", "Planner", "Reviewer", "Tool Runner", "Verifier"], policies.Select(policy => policy.Name).OrderBy(name => name).ToArray());
            Assert.All(policies, policy => Assert.False(policy.AutoRestoreOnVerificationFailure));
            Assert.True(File.Exists(Path.Combine(root, "Data", "agent-execution-policies.json")));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task GetByIdAsync_LoadsPolicyById()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            var policy = await service.GetByIdAsync("toolrunner");

            Assert.NotNull(policy);
            Assert.Equal("ToolRunner", policy!.Id);
            Assert.True(policy.AllowToolCalls);
            Assert.True(policy.AllowShellCommands);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task SaveAsync_PersistsPolicy()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            await service.SaveAsync(new[]
            {
                new AgentExecutionPolicy
                {
                    Id = "Custom",
                    Name = "Custom",
                    Description = "Custom policy",
                    AllowModelCalls = true,
                    AllowToolCalls = true,
                    AllowShellCommands = true,
                    AllowPatchGeneration = true,
                    AllowPatchApply = true,
                    RequireHumanApproval = true,
                    AutoRestoreOnVerificationFailure = true,
                    MaxFilesChanged = 12,
                    MaxPatchOperations = 34,
                    MaxExecutionMinutes = 56,
                    AllowedDirectories = ["src"],
                    BlockedDirectories = ["bin"]
                }
            });

            var reloaded = await service.GetByIdAsync("custom");
            Assert.NotNull(reloaded);
            Assert.Equal(12, reloaded!.MaxFilesChanged);
            Assert.Equal(["src"], reloaded.AllowedDirectories);
            Assert.Equal(["bin"], reloaded.BlockedDirectories);
            Assert.True(reloaded.AutoRestoreOnVerificationFailure);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static AgentExecutionPolicyService CreateService(string root)
    {
        return new AgentExecutionPolicyService(new TestWebHostEnvironment(root));
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-execution-policies-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempProjectRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
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
