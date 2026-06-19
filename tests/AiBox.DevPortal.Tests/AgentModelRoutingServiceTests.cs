using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;
using AiBox.DevPortal.Services;
using AiBox.DevPortal.Services.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace AiBox.DevPortal.Tests;

public sealed class AgentModelRoutingServiceTests
{
    [Fact]
    public async Task ResolveAsync_ExplicitPreferredModelWins()
    {
        await using var context = CreateContext();

        await WriteRecommendationsAsync(context.Root, new AgentModelRecommendation
        {
            AgentRole = AgentMode.Planner,
            RecommendedModel = "recommended-model",
            HasRecommendation = true,
            Reason = "recommended"
        });

        await context.RoutingService.UpsertAsync(new AgentModelAssignment
        {
            Role = AgentMode.Planner,
            PreferredModel = "preferred-model",
            FallbackModel = "fallback-model",
            AllowFallback = true,
            UseRecommendedModel = true
        });

        var routed = await context.RoutingService.ResolveAsync(
            AgentMode.Planner,
            ["preferred-model", "recommended-model", "fallback-model"]);

        Assert.Equal("preferred-model", routed.SelectedModel);
        Assert.False(routed.FallbackUsed);
        Assert.Contains("preferred model", routed.RoutingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_RecommendationUsedWhenEnabled()
    {
        await using var context = CreateContext();

        await WriteRecommendationsAsync(context.Root, new AgentModelRecommendation
        {
            AgentRole = AgentMode.Verifier,
            RecommendedModel = "recommended-model",
            HasRecommendation = true,
            Reason = "recommended"
        });

        await context.RoutingService.UpsertAsync(new AgentModelAssignment
        {
            Role = AgentMode.Verifier,
            PreferredModel = string.Empty,
            FallbackModel = "fallback-model",
            AllowFallback = true,
            UseRecommendedModel = true
        });

        var routed = await context.RoutingService.ResolveAsync(
            AgentMode.Verifier,
            ["recommended-model", "fallback-model"]);

        Assert.Equal("recommended-model", routed.SelectedModel);
        Assert.False(routed.FallbackUsed);
        Assert.Contains("recommendation", routed.RoutingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_FallbackUsedWhenRecommendationMissing()
    {
        await using var context = CreateContext();

        await context.RoutingService.UpsertAsync(new AgentModelAssignment
        {
            Role = AgentMode.Reviewer,
            PreferredModel = string.Empty,
            FallbackModel = "fallback-model",
            AllowFallback = true,
            UseRecommendedModel = true
        });

        var routed = await context.RoutingService.ResolveAsync(
            AgentMode.Reviewer,
            ["fallback-model"]);

        Assert.Equal("fallback-model", routed.SelectedModel);
        Assert.True(routed.FallbackUsed);
        Assert.Contains("fallback", routed.RoutingReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveAsync_UnresolvedWhenNoModelExists()
    {
        await using var context = CreateContext();

        await context.RoutingService.UpsertAsync(new AgentModelAssignment
        {
            Role = AgentMode.ToolRunner,
            PreferredModel = string.Empty,
            FallbackModel = string.Empty,
            AllowFallback = false,
            UseRecommendedModel = true
        });

        var routed = await context.RoutingService.ResolveAsync(AgentMode.ToolRunner);

        Assert.Empty(routed.SelectedModel);
        Assert.False(routed.FallbackUsed);
        Assert.Contains("fallback is disabled", routed.RoutingReason, StringComparison.OrdinalIgnoreCase);
    }

    private static TestContextBundle CreateContext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agent-model-routing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var environment = new TestWebHostEnvironment(root);
        var profileService = new AgentModeProfileService(environment);
        var recommendationService = new AgentModelRecommendationService(environment);
        var routingService = new AgentModelRoutingService(profileService, recommendationService, environment);

        return new TestContextBundle(root, routingService);
    }

    private static async Task WriteRecommendationsAsync(string root, params AgentModelRecommendation[] recommendations)
    {
        var path = Path.Combine(root, "Data", "agent-model-recommendations.json");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, recommendations, options);
    }

    private sealed record TestContextBundle(string Root, AgentModelRoutingService RoutingService) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AiBox.DevPortal.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
        public string EnvironmentName { get; set; } = "Development";
    }
}
