namespace AiBox.DevPortal.Models;

public sealed class TaskSliceVerificationLoopResult
{
    public bool Success { get; set; }
    public int Attempts { get; set; }
    public IReadOnlyList<TaskSliceExecutionResult> VerificationResults { get; set; } = [];
    public string FinalMessage { get; set; } = string.Empty;
    public TaskPlanSlice? GeneratedFixSlice { get; set; }
    public TaskSliceRiskAnalysis? FinalRiskAnalysis { get; set; }
}
