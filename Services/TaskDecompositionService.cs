using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class TaskDecompositionService
{
    private readonly RiskAnalysisService riskAnalysisService;

    public TaskDecompositionService(RiskAnalysisService riskAnalysisService)
    {
        this.riskAnalysisService = riskAnalysisService;
    }

    public TaskPlan BuildPlan(string originalRequest)
    {
        var request = originalRequest?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new ArgumentException("Task request is required.", nameof(originalRequest));
        }

        var planId = Guid.NewGuid().ToString("N");
        var slice = new TaskPlanSlice
        {
            Id = Guid.NewGuid().ToString("N"),
            PlanId = planId,
            Title = "Implement requested task",
            Goal = string.IsNullOrWhiteSpace(request)
                ? "No task was provided."
                : request,
            Description = string.IsNullOrWhiteSpace(request)
                ? "No task was provided."
                : request,
            Status = TaskSliceStatus.Pending,
            TargetFiles = [],
            InstructionFiles = [],
            AllowedChangeType = AllowedChangeType.Any,
            MustNotChange =
            [
                "Patch apply logic",
                "History logic",
                "Agent profiles"
            ],
            VerificationCommands =
            [
                "dotnet build"
            ],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var riskAnalysis = riskAnalysisService.Analyze(slice);
        slice.RiskLevel = riskAnalysis.RiskLevel;
        slice.RiskScore = riskAnalysis.TotalScore;
        slice.RiskSummary = riskAnalysis.Summary;

        return new TaskPlan
        {
            Id = planId,
            OriginalRequest = request,
            CreatedAtUtc = DateTime.UtcNow,
            Slices =
            [
                slice
            ]
        };
    }
}
