namespace AiBox.DevPortal.Services;

public static class ConfigurationExtensions
{
    public static string GetComfyBaseUrl(this IConfiguration configuration)
    {
        return configuration["AiBox:ComfyUI:BaseUrl"]
            ?? configuration["AiBox:ComfyUrl"]
            ?? "http://localhost:8188";
    }

    public static string GetOllamaBaseUrl(this IConfiguration configuration)
    {
        return configuration["AiBox:Ollama:BaseUrl"]
            ?? configuration["Ollama:BaseUrl"]
            ?? configuration["AiBox:OllamaUrl"]
            ?? "http://localhost:11434";
    }
}
