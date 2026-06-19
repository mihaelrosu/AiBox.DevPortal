using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelRecommendationService(IWebHostEnvironment environment)
{
    private const string BenchmarkFileName = "agent-model-benchmark-runs.json";
    private const string ComparisonFileName = "agent-model-comparison-runs.json";
    private const string RecommendationFileName = "agent-model-recommendations.json";

    private static readonly JsonSerializerOptions BenchmarkJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions ComparisonJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions RecommendationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentModelRecommendation>> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadUnlockedAsync(cancellationToken))
                .OrderBy(item => item.AgentRole)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentModelRecommendation?> GetLatestByRoleAsync(AgentMode role, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadUnlockedAsync(cancellationToken))
                .Where(item => item.AgentRole == role)
                .OrderByDescending(item => item.TimestampUtc)
                .Select(Clone)
                .FirstOrDefault();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<IReadOnlyList<AgentModelRecommendation>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var benchmarkRuns = await LoadBenchmarkRunsAsync(cancellationToken);
        var comparisonRuns = await LoadComparisonRunsAsync(cancellationToken);
        var recommendations = BuildRecommendations(benchmarkRuns, comparisonRuns);

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync(recommendations.ToList(), cancellationToken);
        }
        finally
        {
            FileLock.Release();
        }

        return recommendations.Select(Clone).ToArray();
    }

    private async Task<List<AgentModelRecommendation>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentModelRecommendation>>(stream, RecommendationJsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentModelRecommendation> items, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, items, RecommendationJsonOptions, cancellationToken);
    }

    private async Task<IReadOnlyList<AgentModelBenchmarkRun>> LoadBenchmarkRunsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "Data", BenchmarkFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentModelBenchmarkRun>>(stream, BenchmarkJsonOptions, cancellationToken) ?? [];
    }

    private async Task<IReadOnlyList<AgentModelComparisonRun>> LoadComparisonRunsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "Data", ComparisonFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentModelComparisonRun>>(stream, ComparisonJsonOptions, cancellationToken) ?? [];
    }

    private IReadOnlyList<AgentModelRecommendation> BuildRecommendations(
        IReadOnlyList<AgentModelBenchmarkRun> benchmarkRuns,
        IReadOnlyList<AgentModelComparisonRun> comparisonRuns)
    {
        var recommendations = new List<AgentModelRecommendation>();

        foreach (var role in Enum.GetValues<AgentMode>())
        {
            var roleBenchmarkRuns = benchmarkRuns.Where(item => item.AgentRole == role).ToArray();
            var roleComparisonRuns = comparisonRuns.Where(item => item.AgentRole == role).ToArray();

            if (roleBenchmarkRuns.Length == 0 && roleComparisonRuns.Length == 0)
            {
                recommendations.Add(CreateEmptyRecommendation(role, "No benchmark or comparison data is available for this role."));
                continue;
            }

            var modelScores = new Dictionary<string, ModelScore>(StringComparer.OrdinalIgnoreCase);
            foreach (var run in roleBenchmarkRuns)
            {
                var modelName = NormalizeModelName(run.ModelName);
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    continue;
                }

                var score = GetOrCreate(modelScores, modelName);
                score.SourceRunCount++;
                score.BenchmarkRunCount++;
                score.TotalDurationMs += Math.Max(0, run.DurationMs);
                score.DurationSamples++;

                if (run.Success)
                {
                    score.SuccessCount++;
                    score.Score += SuccessfulRunWeight;
                }
                else
                {
                    score.FailureCount++;
                    score.Score -= FailedRunWeight;
                }
            }

            foreach (var comparison in roleComparisonRuns)
            {
                var winner = NormalizeModelName(comparison.BestModel);
                if (string.IsNullOrWhiteSpace(winner))
                {
                    continue;
                }

                var score = GetOrCreate(modelScores, winner);
                score.SourceRunCount++;
                score.ComparisonWinCount++;
                score.Score += ComparisonWinWeight;
            }

            if (modelScores.Count == 0)
            {
                recommendations.Add(CreateEmptyRecommendation(role, "No usable benchmark or comparison model data was found for this role."));
                continue;
            }

            foreach (var score in modelScores.Values)
            {
                score.Score += CalculateDurationBonus(score);
            }

            var best = modelScores.Values
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.SuccessCount)
                .ThenBy(item => item.AverageDurationMs)
                .ThenBy(item => item.ModelName, StringComparer.OrdinalIgnoreCase)
                .First();

            recommendations.Add(new AgentModelRecommendation
            {
                AgentRole = role,
                RecommendedModel = best.ModelName,
                Score = Math.Round(best.Score, 2, MidpointRounding.AwayFromZero),
                Reason = BuildReason(best),
                SourceRunCount = roleBenchmarkRuns.Length + roleComparisonRuns.Length,
                HasRecommendation = true
            });
        }

        return recommendations;
    }

    private static string BuildReason(ModelScore score)
    {
        var averageDurationText = score.DurationSamples == 0
            ? "n/a"
            : $"{score.AverageDurationMs:0} ms";

        return $"Selected from {score.SuccessCount} successful benchmark(s), {score.FailureCount} failed benchmark(s), {score.ComparisonWinCount} comparison win(s), and an average duration of {averageDurationText}.";
    }

    private static double CalculateDurationBonus(ModelScore score)
    {
        if (score.DurationSamples == 0)
        {
            return 0;
        }

        return 10000d / (1d + score.AverageDurationMs);
    }

    private static AgentModelRecommendation CreateEmptyRecommendation(AgentMode role, string reason)
    {
        return new AgentModelRecommendation
        {
            AgentRole = role,
            RecommendedModel = string.Empty,
            Score = 0,
            Reason = reason,
            SourceRunCount = 0,
            HasRecommendation = false
        };
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", RecommendationFileName);
    }

    private static string NormalizeModelName(string? modelName)
    {
        return string.IsNullOrWhiteSpace(modelName) ? string.Empty : modelName.Trim();
    }

    private static ModelScore GetOrCreate(Dictionary<string, ModelScore> modelScores, string modelName)
    {
        if (!modelScores.TryGetValue(modelName, out var score))
        {
            score = new ModelScore { ModelName = modelName };
            modelScores[modelName] = score;
        }

        return score;
    }

    private static AgentModelRecommendation Clone(AgentModelRecommendation item)
    {
        return new AgentModelRecommendation
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            AgentRole = item.AgentRole,
            RecommendedModel = item.RecommendedModel,
            Score = item.Score,
            Reason = item.Reason,
            SourceRunCount = item.SourceRunCount,
            HasRecommendation = item.HasRecommendation
        };
    }

    private static readonly double SuccessfulRunWeight = 1000d;
    private static readonly double FailedRunWeight = 500d;
    private static readonly double ComparisonWinWeight = 300d;

    private sealed class ModelScore
    {
        public string ModelName { get; set; } = string.Empty;
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public int ComparisonWinCount { get; set; }
        public int BenchmarkRunCount { get; set; }
        public int SourceRunCount { get; set; }
        public double Score { get; set; }
        public double TotalDurationMs { get; set; }
        public int DurationSamples { get; set; }
        public double AverageDurationMs => DurationSamples == 0 ? 0 : TotalDurationMs / DurationSamples;
    }
}
