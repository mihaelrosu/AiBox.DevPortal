using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class RiskAnalysisService
{
    public RiskAnalysisResult Analyze(TaskPlanSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var factors = new List<RiskFactor>();
        var score = 0;
        var affectedFiles = GetAffectedFiles(slice);

        AddManyFilesFactor(affectedFiles, factors, ref score);
        AddPathBasedFactors(affectedFiles, factors, ref score);
        AddOperationFactors(slice, factors, ref score);

        var riskLevel = ToRiskLevel(score);
        var requiresManualApproval = riskLevel is RiskLevel.High or RiskLevel.Critical;

        return new RiskAnalysisResult
        {
            RiskLevel = riskLevel,
            TotalScore = score,
            Factors = factors,
            RequiresManualApproval = requiresManualApproval,
            Summary = BuildSummary(riskLevel, score, factors)
        };
    }

    public TaskPlanRiskSummary Analyze(TaskPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sliceAnalyses = plan.Slices.Select(Analyze).ToArray();
        var highest = sliceAnalyses.OrderByDescending(item => item.TotalScore).FirstOrDefault();
        var manualApprovalCount = sliceAnalyses.Count(item => item.RequiresManualApproval);
        var totalScore = sliceAnalyses.Sum(item => item.TotalScore);
        var combinedFactors = sliceAnalyses
            .SelectMany(item => item.Factors)
            .Select(CloneFactor)
            .ToArray();
        var combinedRiskLevel = ToRiskLevel(totalScore);

        return new TaskPlanRiskSummary
        {
            PlanId = plan.Id,
            TotalSlices = plan.Slices.Count,
            RiskLevel = combinedRiskLevel,
            TotalScore = totalScore,
            Factors = combinedFactors,
            RequiresManualApproval = combinedRiskLevel is RiskLevel.High or RiskLevel.Critical || manualApprovalCount > 0,
            HighestRiskLevel = highest?.RiskLevel ?? RiskLevel.Low,
            HighestScore = highest?.TotalScore ?? 0,
            ManualApprovalRequiredCount = manualApprovalCount,
            Summary = BuildPlanSummary(plan, sliceAnalyses, manualApprovalCount, totalScore, combinedRiskLevel)
        };
    }

    private static void AddManyFilesFactor(
        IReadOnlyList<string> affectedFiles,
        List<RiskFactor> factors,
        ref int score)
    {
        if (affectedFiles.Count < 4)
        {
            return;
        }

        var factorScore = affectedFiles.Count switch
        {
            >= 11 => 18,
            >= 7 => 12,
            _ => 6
        };

        score += factorScore;
        factors.Add(new RiskFactor
        {
            Name = "Many affected files",
            Score = factorScore,
            Summary = $"Touches {affectedFiles.Count} files.",
            AffectedFiles = affectedFiles
        });
    }

    private static void AddPathBasedFactors(
        IReadOnlyList<string> affectedFiles,
        List<RiskFactor> factors,
        ref int score)
    {
        var programFiles = FilterFiles(affectedFiles, path => Path.GetFileName(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
        AddFactorIfAny(
            programFiles,
            "Program.cs changes",
            12,
            "Changes application startup or hosting configuration.",
            factors,
            ref score);

        var authFiles = FilterFiles(affectedFiles, path => ContainsAny(path, "auth", "authentication", "identity", "signin", "security"));
        AddFactorIfAny(
            authFiles,
            "Authentication or identity files",
            20,
            "Touches authentication or identity-related code.",
            factors,
            ref score);

        var databaseFiles = FilterFiles(affectedFiles, path => ContainsAny(path, "dbcontext", "migration", "database", "efcore", "sql"));
        AddFactorIfAny(
            databaseFiles,
            "Database or migration files",
            16,
            "Touches database context or migration code.",
            factors,
            ref score);

        var serviceFiles = FilterFiles(affectedFiles, path =>
            path.Contains("/Services/", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(path, "dependencyinjection", "servicecollection", "serviceprovider"));
        AddFactorIfAny(
            serviceFiles,
            "Service or DI changes",
            8,
            "Touches service registration or application services.",
            factors,
            ref score);

        var protectedFiles = FilterFiles(affectedFiles, path =>
            Path.GetFileName(path).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(path, "secrets", "keyvault", "certificate", "credential", "launchsettings", "web.config") ||
            path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        AddFactorIfAny(
            protectedFiles,
            "Protected or security-sensitive files",
            18,
            "Touches a protected or security-sensitive file.",
            factors,
            ref score);
    }

    private static void AddOperationFactors(
        TaskPlanSlice slice,
        List<RiskFactor> factors,
        ref int score)
    {
        if (slice.AllowedChangeType != AllowedChangeType.Remove)
        {
            return;
        }

        const int factorScore = 15;
        score += factorScore;
        factors.Add(new RiskFactor
        {
            Name = "Delete or remove operations",
            Score = factorScore,
            Summary = "Slice allows deletion or removal of existing content."
        });
    }

    private static void AddFactorIfAny(
        IReadOnlyList<string> files,
        string name,
        int factorScore,
        string summary,
        List<RiskFactor> factors,
        ref int score)
    {
        if (files.Count == 0)
        {
            return;
        }

        score += factorScore;
        factors.Add(new RiskFactor
        {
            Name = name,
            Score = factorScore,
            Summary = summary,
            AffectedFiles = files
        });
    }

    private static IReadOnlyList<string> FilterFiles(
        IReadOnlyList<string> files,
        Func<string, bool> predicate)
    {
        return files.Where(predicate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> GetAffectedFiles(TaskPlanSlice slice)
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

    private static RiskLevel ToRiskLevel(int score)
    {
        return score switch
        {
            >= 81 => RiskLevel.Critical,
            >= 51 => RiskLevel.High,
            >= 21 => RiskLevel.Medium,
            _ => RiskLevel.Low
        };
    }

    private static string BuildSummary(
        RiskLevel riskLevel,
        int score,
        IReadOnlyList<RiskFactor> factors)
    {
        if (factors.Count == 0)
        {
            return $"Risk {riskLevel} ({score}): no significant factors detected.";
        }

        var topFactors = factors
            .Select(factor => factor.Name)
            .Take(3)
            .ToArray();

        return topFactors.Length == 0
            ? $"Risk {riskLevel} ({score})."
            : $"Risk {riskLevel} ({score}): {string.Join(", ", topFactors)}.";
    }

    private static RiskFactor CloneFactor(RiskFactor factor)
    {
        return new RiskFactor
        {
            Name = factor.Name,
            Score = factor.Score,
            Summary = factor.Summary,
            AffectedFiles = factor.AffectedFiles
        };
    }

    private static string BuildPlanSummary(
        TaskPlan plan,
        IReadOnlyList<RiskAnalysisResult> analyses,
        int manualApprovalCount,
        int totalScore,
        RiskLevel combinedRiskLevel)
    {
        if (analyses.Count == 0)
        {
            return $"Plan {plan.Id} has no slices to analyze.";
        }

        return $"Plan {plan.Id} includes {analyses.Count} slice(s); score {totalScore} ({combinedRiskLevel}); {manualApprovalCount} require manual approval.";
    }
}
