using System.Text.Json.Serialization;

namespace AiBox.DevPortal.Models.Ollama;

public sealed class OllamaGenerateResponse
{
    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; set; }
}
