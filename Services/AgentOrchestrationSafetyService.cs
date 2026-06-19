using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class AgentOrchestrationSafetyService(IWebHostEnvironment environment)
{
    private const string ReportsFileName = "agent-orchestration-safety-reports.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<AgentOrchestrationSafetyReport>> GetLatestAsync(int take = 25, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(Math.Max(1, take))
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationSafetyReport?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var report = (await LoadAsync(cancellationToken))
                .Where(item => item.RunId.Equals(runId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();

            return report is null ? null : Clone(report);
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<AgentOrchestrationSafetyReport> GenerateAsync(
        string runId,
        string taskName,
        TaskPlanSlice slice,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Run id is required.", nameof(runId));
        }

        if (string.IsNullOrWhiteSpace(taskName))
        {
            throw new ArgumentException("Task name is required.", nameof(taskName));
        }

        ArgumentNullException.ThrowIfNull(slice);

        var files = NormalizeChangedFiles(changedFiles);
        var reasons = new List<string>();
        var blocksAutoApply = slice.RiskLevel == RiskLevel.Critical;
        var requiresManualApproval = slice.RiskLevel is RiskLevel.High or RiskLevel.Critical;

        if (slice.RiskLevel == RiskLevel.Critical)
        {
            reasons.Add("Critical risk blocks auto apply and cannot be applied.");
        }

        if (slice.RiskLevel == RiskLevel.High)
        {
            reasons.Add("High risk requires approval.");
        }

        if (files.Count > 10)
        {
            requiresManualApproval = true;
            reasons.Add("More than 10 changed files requires approval.");
        }

        if (files.Any(IsProgramFile))
        {
            requiresManualApproval = true;
            reasons.Add("Program.cs changes require approval.");
        }

        if (files.Any(IsSecuritySensitiveFile))
        {
            requiresManualApproval = true;
            reasons.Add("Authentication, identity, or security changes require approval.");
        }

        var summary = BuildSummary(blocksAutoApply, requiresManualApproval, reasons);

        var report = new AgentOrchestrationSafetyReport
        {
            RunId = runId.Trim(),
            TaskName = taskName.Trim(),
            HighestRiskLevel = slice.RiskLevel,
            TotalChangedFiles = files.Count,
            RequiresManualApproval = requiresManualApproval,
            BlocksAutoApply = blocksAutoApply,
            Reasons = reasons,
            CreatedAtUtc = DateTime.UtcNow,
            Summary = summary
        };

        await FileLock.WaitAsync(cancellationToken);
        try
        {
            var items = await LoadAsync(cancellationToken);
            items.Add(Clone(report));
            await SaveAsync(items, cancellationToken);
            return Clone(report);
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static string BuildSummary(bool blocksAutoApply, bool requiresManualApproval, IReadOnlyList<string> reasons)
    {
        if (blocksAutoApply)
        {
            return reasons.Count == 0
                ? "Safety review blocked auto apply: critical risk cannot be applied."
                : $"Safety review blocked auto apply: {string.Join(" ", reasons)}";
        }

        if (requiresManualApproval)
        {
            return reasons.Count == 0
                ? "Safety review requires manual approval."
                : $"Safety review requires manual approval: {string.Join(" ", reasons)}";
        }

        return reasons.Count == 0
            ? "Safety review allowed auto apply."
            : $"Safety review allowed auto apply: {string.Join(" ", reasons)}";
    }

    private static bool IsProgramFile(string path)
    {
        return Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSecuritySensitiveFile(string path)
    {
        return path.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("identity", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("security", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("signin", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<AgentOrchestrationSafetyReport>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetReportsPath();
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<AgentOrchestrationSafetyReport>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAsync(List<AgentOrchestrationSafetyReport> reports, CancellationToken cancellationToken)
    {
        var path = GetReportsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, reports, JsonOptions, cancellationToken);
    }

    private string GetReportsPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", ReportsFileName);
    }

    private static IReadOnlyList<string> NormalizeChangedFiles(IReadOnlyList<string> changedFiles)
    {
        return (changedFiles ?? [])
            .Select(path => path.Replace('\\', '/').Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AgentOrchestrationSafetyReport Clone(AgentOrchestrationSafetyReport report)
    {
        return new AgentOrchestrationSafetyReport
        {
            Id = report.Id,
            RunId = report.RunId,
            TaskName = report.TaskName,
            HighestRiskLevel = report.HighestRiskLevel,
            TotalChangedFiles = report.TotalChangedFiles,
            RequiresManualApproval = report.RequiresManualApproval,
            BlocksAutoApply = report.BlocksAutoApply,
            Reasons = [.. report.Reasons ?? []],
            CreatedAtUtc = report.CreatedAtUtc,
            Summary = report.Summary
        };
    }
}
