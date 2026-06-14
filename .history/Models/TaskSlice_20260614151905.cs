using System;

namespace AiBox.DevPortal.Models; 
public class TaskSlice {
    public string Title { get; set; }
    public string Goal { get; set; }
    public List<string> TargetFiles { get; set; }
    public List<string> InstructionFiles { get; set; }
    public AllowedChangeType AllowedChangeType { get; set; }= AllowedChangeType.Add;
    public List<string> MustNotChange { get; set; }
    public List<string> VerificationCommands { get; set; }
}