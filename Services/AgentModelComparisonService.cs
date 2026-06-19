using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelComparisonService(
    AgentModelBenchmarkService benchmarkService,
    IWebHostEnvironment environment)
{
    private const string ComparisonFileName = "agent-model-comparison-runs.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentModelComparisonRun>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadUnlockedAsync(cancellationToken))
                .OrderByDescending(item => item.TimestampUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelComparisonRun> RunComparisonAsync(
        AgentMode agentRole,
        IReadOnlyCollection<string> modelNames,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A comparison prompt is required.", nameof(prompt));
        }

        var models = NormalizeModels(modelNames);
        if (models.Count == 0)
        {
            throw new ArgumentException("At least one comparison model is required.", nameof(modelNames));
        }

        var benchmarkRuns = new List<AgentModelBenchmarkRun>();
        foreach (var model in models)
        {
            var run = await benchmarkService.RunBenchmarkAsync(agentRole, model, prompt, cancellationToken);
            benchmarkRuns.Add(run);
        }

        var bestRun = SelectBestRun(benchmarkRuns);
        var comparison = new AgentModelComparisonRun
        {
            AgentRole = agentRole,
            Prompt = prompt.Trim(),
            ComparedModels = models,
            BenchmarkRunIds = [.. benchmarkRuns.Select(run => run.Id)],
            BestModel = bestRun.ModelName,
            BestModelReason = BuildBestModelReason(bestRun, benchmarkRuns),
            TimestampUtc = DateTime.UtcNow
        };

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadUnlockedAsync(cancellationToken);
            var saved = Clone(comparison);
            if (string.IsNullOrWhiteSpace(saved.Id))
            {
                saved.Id = Guid.NewGuid().ToString("N");
            }

            if (saved.TimestampUtc == default)
            {
                saved.TimestampUtc = DateTime.UtcNow;
            }

            items.Add(saved);
            await SaveAsync(items, cancellationToken);
            comparison = saved;
        }
        finally
        {
            FileLock.Release();
        }

        return Clone(comparison);
    }

    private static List<string> NormalizeModels(IReadOnlyCollection<string> modelNames)
    {
        return modelNames
            .Select(model => string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim())
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentModelBenchmarkRun SelectBestRun(IReadOnlyList<AgentModelBenchmarkRun> runs)
    {
        return runs
            .OrderByDescending(item => item.Success)
            .ThenBy(item => item.DurationMs)
            .ThenByDescending(item => item.OutputLength)
            .First();
    }

    private static string BuildBestModelReason(AgentModelBenchmarkRun bestRun, IReadOnlyList<AgentModelBenchmarkRun> allRuns)
    {
        var successCount = allRuns.Count(item => item.Success);
        if (bestRun.Success)
        {
            if (successCount == 1)
            {
                return $"Best because it was the only successful benchmark, completed in {bestRun.DurationMs} ms, with {bestRun.OutputLength} characters.";
            }

            return $"Best because it succeeded with the shortest duration of {bestRun.DurationMs} ms and {bestRun.OutputLength} characters of output.";
        }

        return $"Best available model after comparing failed runs; it completed in {bestRun.DurationMs} ms and produced {bestRun.OutputLength} characters.";
    }

    private async Task<List<AgentModelComparisonRun>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentModelComparisonRun>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentModelComparisonRun> items, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", ComparisonFileName);
    }

    private static AgentModelComparisonRun Clone(AgentModelComparisonRun item)
    {
        return new AgentModelComparisonRun
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            AgentRole = item.AgentRole,
            Prompt = item.Prompt,
            ComparedModels = [.. item.ComparedModels ?? []],
            BenchmarkRunIds = [.. item.BenchmarkRunIds ?? []],
            BestModel = item.BestModel,
            BestModelReason = item.BestModelReason
        };
    }
}
