using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ImageToolService(IComfyUiService comfyUiService) : IImageToolService
{
    public async Task<ComfyUiGenerationResult> TextToImageAsync(TextToImageToolRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePrompt(request.Prompt);
        ValidateDimensions(request.Width, request.Height);
        ValidateSteps(request.Steps);

        var model = NormalizeModel(request.Model);
        var seed = ComfyUiWorkflowActions.ResolveSeed(request.Seed);
        var workflowName = model == "flux" ? "07_flux_text2image.json" : "01_sdxl_text2image.json";
        var workflow = ComfyUiWorkflowActions.Parse(await comfyUiService.GetWorkflowAsync(workflowName, cancellationToken));

        ComfyUiWorkflowActions.ApplyPrompts(workflow, request.Prompt, request.NegativePrompt);
        ComfyUiWorkflowActions.ApplyLatentSize(workflow, request.Width, request.Height);

        if (model == "flux")
        {
            ComfyUiWorkflowActions.ApplySampler(workflow, seed, request.Steps);
        }
        else
        {
            ComfyUiWorkflowActions.ApplySampler(workflow, seed, request.Steps, request.Cfg, request.Sampler, request.Scheduler);
        }

        return await QueueAsync(workflow, seed, cancellationToken);
    }

    public async Task<ComfyUiGenerationResult> ImageToImageAsync(
        ImageToImageToolRequest request,
        string fileName,
        Stream image,
        CancellationToken cancellationToken = default)
    {
        ValidatePrompt(request.Prompt);
        ValidateSteps(request.Steps);
        ValidateRange(request.Denoise, 0, 1, nameof(request.Denoise));

        var model = NormalizeModel(request.Model);
        var imageName = await comfyUiService.SaveInputImageAsync(fileName, image, cancellationToken);
        var seed = ComfyUiWorkflowActions.ResolveSeed(request.Seed);
        var workflowName = model == "flux" ? "08_flux_image2image.json" : "02_sdxl_image2image.json";
        var workflow = ComfyUiWorkflowActions.Parse(await comfyUiService.GetWorkflowAsync(workflowName, cancellationToken));

        ComfyUiWorkflowActions.ApplyLoadImage(workflow, imageName);
        ComfyUiWorkflowActions.ApplyPrompts(workflow, request.Prompt, request.NegativePrompt);

        if (model == "flux")
        {
            ComfyUiWorkflowActions.ApplyFluxModels(workflow, "flux-2-klein-base-9b-fp8.safetensors", "qwen_3_8b_fp8mixed.safetensors");
            ComfyUiWorkflowActions.ApplySampler(workflow, seed, request.Steps);
        }
        else
        {
            ComfyUiWorkflowActions.ApplySampler(workflow, seed, steps: request.Steps, denoise: request.Denoise);
        }

        return await QueueAsync(workflow, seed, cancellationToken);
    }

    public async Task<ComfyUiGenerationResult> FaceDetailerAsync(
        FaceDetailerToolRequest request,
        string fileName,
        Stream image,
        CancellationToken cancellationToken = default)
    {
        ValidatePrompt(request.Prompt);
        ValidateRange(request.Denoise, 0, 1, nameof(request.Denoise));

        var imageName = await comfyUiService.SaveInputImageAsync(fileName, image, cancellationToken);
        var seed = ComfyUiWorkflowActions.ResolveSeed(request.Seed);
        var workflow = ComfyUiWorkflowActions.Parse(await comfyUiService.GetWorkflowAsync("05_sdxl_face_detailer.json", cancellationToken));

        ComfyUiWorkflowActions.ApplyLoadImage(workflow, imageName);
        ComfyUiWorkflowActions.ApplyPrompts(workflow, request.Prompt, request.NegativePrompt);
        ComfyUiWorkflowActions.ApplySampler(workflow, seed, denoise: request.Denoise);

        return await QueueAsync(workflow, seed, cancellationToken);
    }

    public async Task<ComfyUiGenerationResult> UpscaleAsync(
        UpscaleToolRequest request,
        string fileName,
        Stream image,
        CancellationToken cancellationToken = default)
    {
        if (request.Scale is < 1 or > 8)
        {
            throw new ArgumentException("Scale must be between 1 and 8.", nameof(request.Scale));
        }

        var upscaler = request.Upscaler;
        if (string.IsNullOrWhiteSpace(upscaler))
        {
            upscaler = (await comfyUiService.GetUpscalersAsync(cancellationToken)).FirstOrDefault()
                ?? throw new InvalidOperationException("No upscale model is installed.");
        }

        var imageName = await comfyUiService.SaveInputImageAsync(fileName, image, cancellationToken);
        var workflow = ComfyUiWorkflowActions.Parse(await comfyUiService.GetWorkflowAsync("04_sdxl_upscale.json", cancellationToken));

        ComfyUiWorkflowActions.ApplyLoadImage(workflow, imageName);
        ComfyUiWorkflowActions.ApplyUpscaler(workflow, upscaler, request.Scale);

        return await QueueAsync(workflow, 0, cancellationToken);
    }

    public async Task<ComfyUiGenerationResult> LoraAsync(LoraToolRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePrompt(request.Prompt);
        ValidateRange(request.Strength, -2, 2, nameof(request.Strength));

        if (string.IsNullOrWhiteSpace(request.Lora))
        {
            throw new ArgumentException("LoRA model is required.", nameof(request.Lora));
        }

        var model = NormalizeModel(request.Model);
        var seed = ComfyUiWorkflowActions.ResolveSeed(request.Seed);
        var workflowName = model == "flux" ? "09_flux_lora.json" : "03_sdxl_lora.json";
        var workflow = ComfyUiWorkflowActions.Parse(await comfyUiService.GetWorkflowAsync(workflowName, cancellationToken));

        ComfyUiWorkflowActions.ApplyPrompts(workflow, request.Prompt, request.NegativePrompt);
        ComfyUiWorkflowActions.ApplyLora(workflow, request.Lora, request.Strength);
        ComfyUiWorkflowActions.ApplySampler(workflow, seed);

        return await QueueAsync(workflow, seed, cancellationToken);
    }

    private Task<ComfyUiGenerationResult> QueueAsync(System.Text.Json.Nodes.JsonObject workflow, long seed, CancellationToken cancellationToken)
    {
        return comfyUiService.QueuePromptAndWaitForImageAsync(ComfyUiWorkflowActions.Serialize(workflow), seed, cancellationToken);
    }

    private static string NormalizeModel(string model)
    {
        var normalized = model.Trim().ToLowerInvariant();
        return normalized is "sdxl" or "flux"
            ? normalized
            : throw new ArgumentException("Model must be 'sdxl' or 'flux'.", nameof(model));
    }

    private static void ValidatePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width is < 64 or > 4096 || height is < 64 or > 4096)
        {
            throw new ArgumentException("Width and height must be between 64 and 4096.");
        }
    }

    private static void ValidateSteps(int steps)
    {
        if (steps is < 1 or > 150)
        {
            throw new ArgumentException("Steps must be between 1 and 150.", nameof(steps));
        }
    }

    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.", name);
        }
    }
}
