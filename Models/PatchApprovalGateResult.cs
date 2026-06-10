namespace AiBox.DevPortal.Models;

public sealed class PatchApprovalGateResult
{
    public string GateKey { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public bool Blocking { get; set; }
    public bool Warning { get; set; }
}
