using System.Net.Http.Json;
using AiBox.DevPortal.Models;
using AiBox.DevPortal.Models.Ollama;

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

    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("An Ollama model is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("An Ollama prompt is required.", nameof(prompt));
        }

        using var response = await httpClient.PostAsJsonAsync("/api/generate", new OllamaGenerateRequest
        {
            Model = model.Trim(),
            Prompt = prompt.Trim(),
            Stream = false
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(cancellationToken);

        if (string.IsNullOrWhiteSpace(result?.Response))
        {
            throw new InvalidOperationException("Ollama returned an empty response.");
        }

        return result.Response.Trim();
    }
}
