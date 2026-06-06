using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ToolStatusService(HttpClient httpClient) : IToolStatusService
{
    private static readonly ToolDefinition[] Tools =
    [
        new("Open WebUI", "http://ai-box.local:3000"),
        new("ComfyUI", "http://ai-box.local:8188"),
        new("Ollama", "http://ai-box.local:11434"),
        new("Immich", "http://ai-box.local:2283"),
        new("Portainer", "http://ai-box.local:9000"),
        new("n8n", "http://ai-box.local:5678")
    ];

    public async Task<IReadOnlyList<AiBoxToolLink>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var checks = Tools.Select(tool => CheckToolAsync(tool, cancellationToken));
        return await Task.WhenAll(checks);
    }

    private async Task<AiBoxToolLink> CheckToolAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, tool.Url);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return new AiBoxToolLink(tool.Name, tool.Url, response.IsSuccessStatusCode ? "Online" : "Offline");
        }
        catch
        {
            return new AiBoxToolLink(tool.Name, tool.Url, "Offline");
        }
    }

    private sealed record ToolDefinition(string Name, string Url);
}
