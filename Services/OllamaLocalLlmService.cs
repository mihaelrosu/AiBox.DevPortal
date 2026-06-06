using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AiBox.DevPortal.Services;

public sealed class OllamaLocalLlmService(HttpClient httpClient) : ILocalLlmService
{
    public async Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A local LLM model is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A generated step prompt is required.", nameof(prompt));
        }

        using var response = await httpClient.PostAsJsonAsync("/api/generate", new OllamaGenerateRequest
        {
            Model = model.Trim(),
            Prompt = prompt,
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

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string Response { get; set; } = string.Empty;
    }
}
