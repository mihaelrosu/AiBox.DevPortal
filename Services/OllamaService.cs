using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class OllamaService(HttpClient httpClient) : IOllamaService
{
    public async Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var url = httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost:11434";

        try
        {
            using var response = await httpClient.GetAsync("/api/tags", cancellationToken);
            return new ServiceHealth("Ollama", url, response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Online" : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            return new ServiceHealth("Ollama", url, false, exception.Message);
        }
    }
}
