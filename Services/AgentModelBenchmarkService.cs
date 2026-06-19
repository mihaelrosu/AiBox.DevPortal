using System.Diagnostics;
using System.Text.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services;

public sealed class AgentModelBenchmarkService(IOllamaService ollamaService, IWebHostEnvironment environment)
{
    private const string BenchmarkFileName = "agent-model-benchmark-runs.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentModelBenchmarkRun>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
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

    public async Task<AgentModelBenchmarkRun> RunBenchmarkAsync(
        AgentMode agentRole,
        string modelName,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new ArgumentException("A benchmark model name is required.", nameof(modelName));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A benchmark prompt is required.", nameof(prompt));
        }

        var run = new AgentModelBenchmarkRun
        {
            AgentRole = agentRole,
            ModelName = modelName.Trim(),
            Prompt = prompt.Trim(),
            TimestampUtc = DateTime.UtcNow
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = await ollamaService.GenerateAsync(run.ModelName, run.Prompt, cancellationToken);
            run.Success = true;
            run.OutputLength = output.Length;
            run.ErrorMessage = string.Empty;
        }
        catch (Exception exception)
        {
            run.Success = false;
            run.OutputLength = 0;
            run.ErrorMessage = exception.Message;
        }
        finally
        {
            stopwatch.Stop();
            run.DurationMs = Math.Max(0, stopwatch.ElapsedMilliseconds);
            await FileLock.WaitAsync(cancellationToken);
            try
            {
                var items = await LoadUnlockedAsync(cancellationToken);
                var saved = Clone(run);
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
                run = saved;
            }
            finally
            {
                FileLock.Release();
            }
        }

        return Clone(run);
    }

    private async Task<List<AgentModelBenchmarkRun>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentModelBenchmarkRun>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentModelBenchmarkRun> items, CancellationToken cancellationToken)
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
        return Path.Combine(environment.ContentRootPath, "Data", BenchmarkFileName);
    }

    private static AgentModelBenchmarkRun Clone(AgentModelBenchmarkRun item)
    {
        return new AgentModelBenchmarkRun
        {
            Id = item.Id,
            TimestampUtc = item.TimestampUtc,
            AgentRole = item.AgentRole,
            ModelName = item.ModelName,
            Prompt = item.Prompt,
            Success = item.Success,
            DurationMs = item.DurationMs,
            OutputLength = item.OutputLength,
            ErrorMessage = item.ErrorMessage
        };
    }
}
