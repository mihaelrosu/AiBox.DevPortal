using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class ExecutionPolicyProfileServiceTests
{
    [Fact]
    public async Task GetAllAsync_LoadsDefaultProfiles()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            var profiles = await service.GetAllAsync();

            Assert.Equal(3, profiles.Count);
            Assert.Equal(["Aggressive", "Balanced", "Safe"], profiles.Select(profile => profile.Name).ToArray());
            Assert.True(File.Exists(Path.Combine(root, "Data", "execution-policy-profiles.json")));
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task SaveAsync_PersistsProfile()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);
            var saved = await service.SaveAsync(new ExecutionPolicyProfile
            {
                Name = "Custom",
                AllowAutoApply = true,
                AllowCommitAndSync = true,
                RequireHumanApprovalForHighRisk = false,
                AllowProgramCsChanges = true,
                AllowSecurityChanges = true,
                MaxChangedFiles = 42
            });

            Assert.Equal("Custom", saved.Name);
            Assert.True(saved.AllowAutoApply);
            Assert.True(saved.AllowCommitAndSync);
            Assert.False(saved.RequireHumanApprovalForHighRisk);
            Assert.True(saved.AllowProgramCsChanges);
            Assert.True(saved.AllowSecurityChanges);
            Assert.Equal(42, saved.MaxChangedFiles);

            var reloaded = CreateService(root);
            var profile = await reloaded.GetByNameAsync("custom");

            Assert.NotNull(profile);
            Assert.Equal(saved.Name, profile!.Name);
            Assert.True(profile.AllowAutoApply);
            Assert.True(profile.AllowCommitAndSync);
            Assert.False(profile.RequireHumanApprovalForHighRisk);
            Assert.True(profile.AllowProgramCsChanges);
            Assert.True(profile.AllowSecurityChanges);
            Assert.Equal(42, profile.MaxChangedFiles);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    [Fact]
    public async Task GetByNameAsync_LoadsProfileByName()
    {
        var root = CreateTempProjectRoot();

        try
        {
            var service = CreateService(root);

            var profile = await service.GetByNameAsync("balanced");

            Assert.NotNull(profile);
            Assert.Equal("Balanced", profile!.Name);
            Assert.True(profile.AllowCommitAndSync);
            Assert.True(profile.RequireHumanApprovalForHighRisk);
            Assert.True(profile.AllowProgramCsChanges);
            Assert.False(profile.AllowSecurityChanges);
            Assert.Equal(10, profile.MaxChangedFiles);
        }
        finally
        {
            DeleteTempProjectRoot(root);
        }
    }

    private static ExecutionPolicyProfileService CreateService(string root)
    {
        return new ExecutionPolicyProfileService(new TestWebHostEnvironment(root));
    }

    private static string CreateTempProjectRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"execution-policy-profiles-{Guid.NewGuid():N}");
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
