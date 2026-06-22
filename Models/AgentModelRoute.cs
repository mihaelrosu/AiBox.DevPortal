namespace AiBox.DevPortal.Models;

public sealed class AgentModelRoute
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
    public bool AllowTools { get; set; }
    public string ExpectedOutputFormat { get; set; } = "json";
    public string LifecyclePolicy { get; set; } = "fixed-server";
    public bool IsDefaultForCoder { get; set; }
}
