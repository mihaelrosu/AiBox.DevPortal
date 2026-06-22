using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelRouteService(IWebHostEnvironment environment)
{
    private const string FileName = "agent-model-routes.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentModelRoute>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var routes = await LoadOrSeedAsync(cancellationToken);
            return routes.Select(Clone).ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelRoute?> GetDefaultCoderRouteAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var routes = await LoadOrSeedAsync(cancellationToken);
            var route = routes.FirstOrDefault(item => item.IsDefaultForCoder);
            return route is null ? null : Clone(route);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<AgentModelRoute> routes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routes);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await SaveRoutesAsync(routes.Select(Clone).ToList(), cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<AgentModelRoute>> LoadOrSeedAsync(CancellationToken cancellationToken)
    {
        var path = GetRoutesPath();
        if (!File.Exists(path))
        {
            var seeded = CreateSeedRoutes();
            await SaveRoutesAsync(seeded, cancellationToken);
            return seeded;
        }

        var routes = await JsonFileStore.LoadListAsync(path, JsonOptions, cancellationToken, CreateSeedRoutes);
        if (routes.Count == 0)
        {
            routes = CreateSeedRoutes();
            await SaveRoutesAsync(routes, cancellationToken);
        }

        return routes;
    }

    private async Task SaveRoutesAsync(List<AgentModelRoute> routes, CancellationToken cancellationToken)
    {
        await JsonFileStore.SaveListAsync(GetRoutesPath(), routes, JsonOptions, cancellationToken);
    }

    private string GetRoutesPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", FileName);
    }

    private static List<AgentModelRoute> CreateSeedRoutes()
    {
        return
        [
            new AgentModelRoute
            {
                Id = "ollama-open-webui",
                Name = "Ollama / Open WebUI",
                Provider = "Ollama",
                BaseUrl = "http://localhost:11434",
                Model = "dynamic",
                ContextSize = 8192,
                AllowTools = false,
                ExpectedOutputFormat = "free-text",
                LifecyclePolicy = "shared-runtime",
                IsDefaultForCoder = false
            },
            new AgentModelRoute
            {
                Id = "llamacpp-local-coder",
                Name = "llama.cpp / DevPortal Local Coder",
                Provider = "llama.cpp",
                BaseUrl = "http://localhost:8082/v1/chat/completions",
                Model = "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
                ContextSize = 8192,
                AllowTools = false,
                ExpectedOutputFormat = "json",
                LifecyclePolicy = "fixed-server",
                IsDefaultForCoder = true
            }
        ];
    }

    private static AgentModelRoute Clone(AgentModelRoute route)
    {
        return new AgentModelRoute
        {
            Id = route.Id,
            Name = route.Name,
            Provider = route.Provider,
            BaseUrl = route.BaseUrl,
            Model = route.Model,
            ContextSize = route.ContextSize,
            AllowTools = route.AllowTools,
            ExpectedOutputFormat = route.ExpectedOutputFormat,
            LifecyclePolicy = route.LifecyclePolicy,
            IsDefaultForCoder = route.IsDefaultForCoder
        };
    }
}
