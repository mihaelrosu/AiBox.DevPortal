using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class PromptEnhancerService(HttpClient httpClient, IConfiguration configuration) : IPromptEnhancerService
{
    private const string DefaultModel = "gemma4:e2b";
    private const string DefaultNegativePrompt = "low quality, blurry, distorted, deformed, bad anatomy, extra fingers, missing fingers, watermark, text, logo, cropped, jpeg artifacts";

    public async Task<PromptEnhanceResult> ImproveSdxlPromptAsync(string positivePrompt, string? style = null)
    {
        if (string.IsNullOrWhiteSpace(positivePrompt))
        {
            throw new ArgumentException("Enter a positive prompt before improving it.", nameof(positivePrompt));
        }

        var prompt = """
            You are an SDXL prompt enhancer.
            Rewrite the user's idea into a strong SDXL text-to-image prompt.
            Keep the original meaning.
            Add visual details, subject, composition, lighting, camera/lens style, materials, mood, background, and quality tags.
            Do not add text, logos, watermarks, extra fingers, deformed anatomy, or impossible body parts.
            Return only the improved positive prompt.
            Do not explain.
            """;

        if (!string.IsNullOrWhiteSpace(style))
        {
            prompt += $"{Environment.NewLine}Style: {style.Trim()}";
        }

        prompt += $"{Environment.NewLine}User idea: {positivePrompt.Trim()}";

        var response = await GenerateAsync(prompt);
        return new PromptEnhanceResult { Prompt = CleanPrompt(response) };
    }

    public async Task<PromptEnhanceResult> CreateSdxlNegativePromptAsync(string positivePrompt, string? style = null)
    {
        var prompt = $"""
            You are an SDXL negative prompt generator.
            Create a concise, useful SDXL negative prompt that prevents common image defects.
            Include this baseline unless a term is clearly irrelevant: {DefaultNegativePrompt}
            Do not explain.
            Return only the negative prompt.
            """;

        if (!string.IsNullOrWhiteSpace(style))
        {
            prompt += $"{Environment.NewLine}Style: {style.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(positivePrompt))
        {
            prompt += $"{Environment.NewLine}Positive prompt: {positivePrompt.Trim()}";
        }

        var response = await GenerateAsync(prompt);
        var negativePrompt = CleanPrompt(response);

        if (string.IsNullOrWhiteSpace(negativePrompt))
        {
            negativePrompt = DefaultNegativePrompt;
        }

        return new PromptEnhanceResult { Prompt = negativePrompt };
    }

    private async Task<string> GenerateAsync(string prompt)
    {
        var request = new OllamaGenerateRequest
        {
            Model = configuration["AiBox:PromptEnhancerModel"] ?? DefaultModel,
            Prompt = prompt,
            Stream = false
        };

        using var response = await httpClient.PostAsJsonAsync("/api/generate", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
        return result?.Response ?? string.Empty;
    }

    private static string CleanPrompt(string prompt)
    {
        return prompt.Trim().Trim('"', '`');
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = DefaultModel;

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
