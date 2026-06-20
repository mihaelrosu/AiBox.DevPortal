namespace AiBox.DevPortal.Models;

public sealed class ExecutionPolicyProfile
{
    public string Name { get; set; } = string.Empty;
    public bool AllowAutoApply { get; set; }
    public bool AllowCommitAndSync { get; set; }
    public bool RequireHumanApprovalForHighRisk { get; set; } = true;
    public bool AllowProgramCsChanges { get; set; } = true;
    public bool AllowSecurityChanges { get; set; }
    public int MaxChangedFiles { get; set; }
}
