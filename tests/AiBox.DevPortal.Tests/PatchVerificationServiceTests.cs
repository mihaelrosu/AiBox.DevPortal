using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using AiBox.DevPortal.Models.Agents;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class PatchVerificationServiceTests
{
    [Fact]
    public async Task VerifyAsync_RunsDefaultBuildCommand()
    {
        var projectRoot = await CreateTempProjectAsync();
        var historyService = new AgentRunHistoryService(new TestWebHostEnvironment(projectRoot));
        var service = new PatchVerificationService(historyService);

        try
        {
            var result = await service.VerifyAsync(projectRoot);

            Assert.Single(result.Commands);
            Assert.Equal("dotnet build", result.Commands[0].Command);
            Assert.NotNull(result.Summary);
            Assert.NotNull(result.Details);
            Assert.Equal(projectRoot, result.ProjectPath);

            var historyPath = Path.Combine(projectRoot, "Data", "agent-runs.json");
            Assert.True(File.Exists(historyPath));

            var records = await JsonSerializer.DeserializeAsync<List<AgentRunRecord>>(
                File.OpenRead(historyPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                });

            var savedRecord = Assert.Single(records ?? []);
            Assert.Equal("patch-verification", savedRecord.ActionKey);
            Assert.NotNull(savedRecord.ResultText);
            Assert.Equal(result.Details, savedRecord.ResultText);
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
    public async Task VerifyAsync_RejectsBlockedCommands()
    {
        var projectRoot = await CreateTempProjectAsync();
        var historyService = new AgentRunHistoryService(new TestWebHostEnvironment(projectRoot));
        var service = new PatchVerificationService(historyService);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.VerifyAsync(projectRoot, ["rm -rf ."]));

            Assert.Contains("Blocked verification command", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    private static async Task<string> CreateTempProjectAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aibox-patch-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        await File.WriteAllTextAsync(
            Path.Combine(root, "Sample.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(
            Path.Combine(root, "Program.cs"),
            """
            Console.WriteLine("Hello");
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
