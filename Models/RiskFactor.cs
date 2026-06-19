namespace AiBox.DevPortal.Models;

public sealed class RiskFactor
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public string Summary { get; set; } = string.Empty;
    public IReadOnlyList<string> AffectedFiles { get; set; } = [];
}
