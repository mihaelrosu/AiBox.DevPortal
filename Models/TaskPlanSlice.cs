namespace AiBox.DevPortal.Models;

public sealed class TaskPlanSlice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlanId { get; set; } = string.Empty;
    public string PatchPackageId { get; set; } = string.Empty;
    public DateTime? RolledBackAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskSliceStatus Status { get; set; } = TaskSliceStatus.Pending;
    public DateTime? PatchPreviewCreatedAt { get; set; }
    public List<string> TargetFiles { get; set; } = [];
    public IReadOnlyList<string> DependsOnSliceIds { get; set; } = [];
    public List<string> InstructionFiles { get; set; } = [];
    public AllowedChangeType AllowedChangeType { get; set; } = AllowedChangeType.Any;
    public List<string> MustNotChange { get; set; } = [];
    public List<string> VerificationCommands { get; set; } = [];
    public List<string> RelatedFiles { get; set; } = [];
    public RiskLevel RiskLevel { get; set; } = RiskLevel.Low;
    public int RiskScore { get; set; }
    public string RiskSummary { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static implicit operator TaskPlanSlice(TaskSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return new TaskPlanSlice
        {
            PlanId = string.Empty,
            PatchPackageId = string.Empty,
            RolledBackAt = null,
            Title = slice.Title,
            Goal = slice.Goal,
            Description = slice.Goal,
            TargetFiles = [.. slice.TargetFiles],
            InstructionFiles = [.. slice.InstructionFiles],
            AllowedChangeType = slice.AllowedChangeType,
            MustNotChange = [.. slice.MustNotChange],
            VerificationCommands = [.. slice.VerificationCommands],
            RelatedFiles = [.. slice.TargetFiles.Concat(slice.InstructionFiles).Distinct(StringComparer.OrdinalIgnoreCase)],
            RiskLevel = RiskLevel.Low,
            RiskScore = 0,
            RiskSummary = string.Empty,
            Notes = string.Empty,
            Status = TaskSliceStatus.Pending,
            PatchPreviewCreatedAt = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public static implicit operator TaskSlice(TaskPlanSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return new TaskSlice
        {
            Title = slice.Title,
            Goal = string.IsNullOrWhiteSpace(slice.Goal) ? slice.Description : slice.Goal,
            TargetFiles = [.. (slice.TargetFiles.Count > 0 ? slice.TargetFiles : slice.RelatedFiles)],
            InstructionFiles = [.. slice.InstructionFiles],
            AllowedChangeType = slice.AllowedChangeType,
            MustNotChange = [.. slice.MustNotChange],
            VerificationCommands = [.. slice.VerificationCommands]
        };
    }
}
