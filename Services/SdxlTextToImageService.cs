using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class SdxlTextToImageService(HttpClient httpClient, IWebHostEnvironment environment) : ISdxlTextToImageService
{
    private const int MaxHistoryAttempts = 120;
    private static readonly TimeSpan HistoryPollDelay = TimeSpan.FromSeconds(1);

    public async Task<SdxlTextToImageResult> GenerateAsync(SdxlTextToImageRequest request)
    {
        var workflow = await LoadWorkflowAsync();
        var metadata = ApplyRequest(workflow, request);

        using var promptResponse = await httpClient.PostAsJsonAsync("prompt", new { prompt = workflow });
        promptResponse.EnsureSuccessStatusCode();

        var promptPayload = await promptResponse.Content.ReadFromJsonAsync<JsonObject>();
        var promptId = promptPayload?["prompt_id"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(promptId))
        {
            throw new InvalidOperationException("ComfyUI did not return a prompt_id.");
        }

        var image = await WaitForImageAsync(promptId);
        var imageUrl = BuildImageUrl(image);

        return new SdxlTextToImageResult
        {
            PromptId = promptId,
            ImageUrl = imageUrl,
            Seed = metadata.Seed,
            Model = metadata.Model,
            Width = metadata.Width,
            Height = metadata.Height,
            Steps = metadata.Steps,
            Cfg = metadata.Cfg,
            Sampler = metadata.Sampler,
            Scheduler = metadata.Scheduler,
            PositivePrompt = metadata.PositivePrompt,
            NegativePrompt = metadata.NegativePrompt
        };
    }

    private async Task<JsonObject> LoadWorkflowAsync()
    {
        var workflowPath = Path.Combine(environment.ContentRootPath, "Workflows", "sdxl_t2i_api.json");
        await using var stream = File.OpenRead(workflowPath);
        return await JsonNode.ParseAsync(stream) as JsonObject
            ?? throw new InvalidOperationException("Workflow JSON must contain an object at the root.");
    }

    private static AppliedSdxlSettings ApplyRequest(JsonObject workflow, SdxlTextToImageRequest request)
    {
        var positivePromptApplied = false;
        var negativePromptApplied = false;
        var seed = request.Seed < 0 ? Random.Shared.NextInt64(0, long.MaxValue) : request.Seed;
        var sampler = string.Empty;
        var scheduler = string.Empty;

        foreach (var node in workflow.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var classType = node["class_type"]?.GetValue<string>();
            var inputs = node["inputs"] as JsonObject;

            if (string.IsNullOrWhiteSpace(classType) || inputs is null)
            {
                continue;
            }

            switch (classType)
            {
                case "CheckpointLoaderSimple":
                    inputs["ckpt_name"] = request.CheckpointName;
                    break;
                case "CLIPTextEncode" when !positivePromptApplied:
                    inputs["text"] = request.PositivePrompt;
                    positivePromptApplied = true;
                    break;
                case "CLIPTextEncode":
                    inputs["text"] = request.NegativePrompt;
                    negativePromptApplied = true;
                    break;
                case "EmptyLatentImage":
                    inputs["width"] = request.Width;
                    inputs["height"] = request.Height;
                    break;
                case "KSampler":
                    inputs["seed"] = seed;
                    inputs["steps"] = request.Steps;
                    inputs["cfg"] = request.CfgScale;
                    sampler = inputs["sampler_name"]?.GetValue<string>() ?? string.Empty;
                    scheduler = inputs["scheduler"]?.GetValue<string>() ?? string.Empty;
                    break;
            }
        }

        if (!positivePromptApplied || !negativePromptApplied)
        {
            throw new InvalidOperationException("Workflow must contain two CLIPTextEncode nodes for positive and negative prompts.");
        }

        return new AppliedSdxlSettings(
            seed,
            request.CheckpointName,
            request.Width,
            request.Height,
            request.Steps,
            request.CfgScale,
            sampler,
            scheduler,
            request.PositivePrompt,
            request.NegativePrompt);
    }

    private async Task<ComfyImage> WaitForImageAsync(string promptId)
    {
        for (var attempt = 0; attempt < MaxHistoryAttempts; attempt++)
        {
            var history = await httpClient.GetFromJsonAsync<JsonObject>($"history/{Uri.EscapeDataString(promptId)}");
            var image = FindOutputImage(history, promptId);

            if (image is not null)
            {
                return image;
            }

            await Task.Delay(HistoryPollDelay);
        }

        throw new TimeoutException("Timed out waiting for ComfyUI to finish generating an image.");
    }

    private static ComfyImage? FindOutputImage(JsonObject? history, string promptId)
    {
        var outputs = history?[promptId]?["outputs"] as JsonObject;

        if (outputs is null)
        {
            return null;
        }

        foreach (var outputNode in outputs.Select(pair => pair.Value).OfType<JsonObject>())
        {
            if (outputNode["images"] is not JsonArray images)
            {
                continue;
            }

            foreach (var imageNode in images.OfType<JsonObject>())
            {
                var filename = imageNode["filename"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(filename))
                {
                    return new ComfyImage(
                        filename,
                        imageNode["subfolder"]?.GetValue<string>() ?? string.Empty,
                        imageNode["type"]?.GetValue<string>() ?? "output");
                }
            }
        }

        return null;
    }

    private string BuildImageUrl(ComfyImage image)
    {
        var baseUri = httpClient.BaseAddress ?? new Uri("http://localhost:8188");
        var query = string.Create(CultureInfo.InvariantCulture, $"view?filename={Uri.EscapeDataString(image.Filename)}&type={Uri.EscapeDataString(image.Type)}");

        if (!string.IsNullOrWhiteSpace(image.Subfolder))
        {
            query += $"&subfolder={Uri.EscapeDataString(image.Subfolder)}";
        }

        return new Uri(baseUri, query).ToString();
    }

    private sealed record ComfyImage(string Filename, string Subfolder, string Type);
    private sealed record AppliedSdxlSettings(
        long Seed,
        string Model,
        int Width,
        int Height,
        int Steps,
        double Cfg,
        string Sampler,
        string Scheduler,
        string PositivePrompt,
        string NegativePrompt);
}
