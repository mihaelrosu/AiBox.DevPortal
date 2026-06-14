namespace AiBox.DevPortal.Models;

public sealed class TaskSlice
{
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public List<string> TargetFiles { get; set; } = [];
    public List<string> InstructionFiles { get; set; } = [];
    public AllowedChangeType AllowedChangeType { get; set; } = AllowedChangeType.Any;
    public List<string> MustNotChange { get; set; } = [];
    public List<string> VerificationCommands { get; set; } = [];
}