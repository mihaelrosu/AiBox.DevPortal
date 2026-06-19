using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskSliceRiskAnalysisService
{
    public TaskSliceRiskAnalysis Analyze(TaskPlanSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var reasons = new List<string>();
        var score = 0;

        var candidateFiles = GetCandidateFiles(slice);
        if (candidateFiles.Count == 0)
        {
            reasons.Add("No target or related files were identified.");
            score += 3;
        }

        if (slice.AllowedChangeType == AllowedChangeType.Remove)
        {
            reasons.Add("Slice removes existing code or files.");
            score += 2;
        }

        if (candidateFiles.Count > 4)
        {
            reasons.Add("Slice touches multiple files.");
            score += 1;
        }

        foreach (var file in candidateFiles)
        {
            AddFileRisk(file, reasons, ref score);
        }

        if (slice.VerificationCommands.Count == 0)
        {
            reasons.Add("No verification commands were provided.");
            score += 1;
        }

        if (ContainsUrgencyLanguage(slice))
        {
            reasons.Add("Slice notes indicate urgent or risky changes.");
            score += 1;
        }

        var uniqueReasons = reasons
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new TaskSliceRiskAnalysis
        {
            PlanId = slice.PlanId,
            SliceId = slice.Id,
            SliceTitle = slice.Title,
            RiskScore = score,
            RiskLevel = ToRiskLevel(score),
            Reasons = uniqueReasons,
            AnalyzedAtUtc = DateTime.UtcNow
        };
    }

    public IReadOnlyList<TaskSliceRiskAnalysis> Analyze(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return plan.Slices
            .Select(Analyze)
            .ToArray();
    }

    public IReadOnlyDictionary<string, TaskSliceRiskAnalysis> AnalyzeBySliceId(TaskPlan plan)
    {
        return Analyze(plan).ToDictionary(item => item.SliceId, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddFileRisk(string relativePath, List<string> reasons, ref int score)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);

        if (fileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Application startup file.");
            score += 2;
        }

        if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Application settings file.");
            score += 2;
        }

        if (ContainsAny(normalized, "auth", "authentication", "identity", "security", "signin"))
        {
            reasons.Add("Authentication or security code.");
            score += 3;
        }

        if (ContainsAny(normalized, "dbcontext", "migration", "database", "sql", "efcore"))
        {
            reasons.Add("Database or migration code.");
            score += 3;
        }

        if (normalized.Contains("/Pages/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("UI or markup file.");
            score += 1;
        }

        if (normalized.StartsWith("Services/", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("Service layer change.");
            score += 1;
        }
    }

    private static IReadOnlyList<string> GetCandidateFiles(TaskPlanSlice slice)
    {
        var files = slice.TargetFiles.Count > 0 ? slice.TargetFiles : slice.RelatedFiles;

        return files
            .Select(path => path.Replace('\\', '/').Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsUrgencyLanguage(TaskPlanSlice slice)
    {
        var text = string.Join(' ', new[]
        {
            slice.Title,
            slice.Goal,
            slice.Description,
            slice.Notes
        });

        return ContainsAny(text, "urgent", "critical", "hotfix", "risky", "breaking");
    }

    private static TaskSliceRiskLevel ToRiskLevel(int score)
    {
        return score switch
        {
            >= 7 => TaskSliceRiskLevel.Critical,
            >= 4 => TaskSliceRiskLevel.High,
            >= 2 => TaskSliceRiskLevel.Medium,
            _ => TaskSliceRiskLevel.Low
        };
    }
}
