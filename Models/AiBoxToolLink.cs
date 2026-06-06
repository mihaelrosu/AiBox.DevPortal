namespace AiBox.DevPortal.Models;

public sealed record AiBoxToolLink(string ToolName, string Url, string Status)
{
    public bool IsOnline => Status == "Online";
}
