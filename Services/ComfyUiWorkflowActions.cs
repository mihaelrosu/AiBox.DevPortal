using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiBox.DevPortal.Services;

public static class ComfyUiWorkflowActions
{
    public static JsonObject Parse(string json)
    {
        return JsonNode.Parse(json)?.DeepClone() as JsonObject
            ?? throw new InvalidOperationException("Workflow JSON must contain an object at the root.");
    }

    public static string Serialize(JsonObject workflow)
    {
        return workflow.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public static long ResolveSeed(long seed)
    {
        return seed < 0 ? Random.Shared.NextInt64(0, long.MaxValue) : seed;
    }

    public static void ApplyPrompts(JsonObject workflow, string positivePrompt, string? negativePrompt = null)
    {
        var promptInputs = NodesByClass(workflow, "CLIPTextEncode")
            .Select(node => new
            {
                Inputs = node["inputs"] as JsonObject,
                Title = node["_meta"]?["title"]?.GetValue<string>()
            })
            .Where(node => node.Inputs is not null)
            .ToList();

        var positiveInputs = promptInputs.FirstOrDefault(node =>
            node.Title?.Contains("Positive Prompt", StringComparison.OrdinalIgnoreCase) == true)?.Inputs
            ?? promptInputs.FirstOrDefault()?.Inputs;

        if (positiveInputs is null)
        {
            throw new InvalidOperationException("The workflow does not contain a prompt input. Install the required models and replace the placeholder workflow.");
        }

        positiveInputs["text"] = positivePrompt;

        if (negativePrompt is not null)
        {
            foreach (var negativeInputs in promptInputs
                .Where(node => node.Inputs != positiveInputs)
                .Select(node => node.Inputs!))
            {
                negativeInputs["text"] = negativePrompt;
            }
        }
    }

    public static void ApplyLatentSize(JsonObject workflow, int width, int height)
    {
        var applied = false;

        foreach (var inputs in InputsByClass(workflow, "EmptyLatentImage").Concat(InputsByClass(workflow, "EmptyImage")))
        {
            inputs["width"] = width;
            inputs["height"] = height;
            applied = true;
        }

        foreach (var node in NodesByClass(workflow, "PrimitiveInt"))
        {
            var title = node["_meta"]?["title"]?.GetValue<string>();
            if (node["inputs"] is not JsonObject inputs)
            {
                continue;
            }

            if (string.Equals(title, "Width", StringComparison.OrdinalIgnoreCase))
            {
                inputs["value"] = width;
                applied = true;
            }
            else if (string.Equals(title, "Height", StringComparison.OrdinalIgnoreCase))
            {
                inputs["value"] = height;
                applied = true;
            }
        }

        if (!applied)
        {
            throw new InvalidOperationException("The workflow does not contain a configurable image or latent size.");
        }
    }

    public static void ApplySampler(JsonObject workflow, long seed, int? steps = null, double? cfg = null, string? sampler = null, string? scheduler = null, double? denoise = null)
    {
        var applied = false;

        foreach (var inputs in InputsByClass(workflow, "KSampler").Concat(InputsByClass(workflow, "FaceDetailer")))
        {
            inputs["seed"] = seed;
            applied = true;

            if (steps.HasValue)
            {
                inputs["steps"] = steps.Value;
            }

            if (cfg.HasValue)
            {
                inputs["cfg"] = cfg.Value;
            }

            if (!string.IsNullOrWhiteSpace(sampler))
            {
                inputs["sampler_name"] = sampler;
            }

            if (!string.IsNullOrWhiteSpace(scheduler))
            {
                inputs["scheduler"] = scheduler;
            }

            if (denoise.HasValue)
            {
                inputs["denoise"] = denoise.Value;
            }
        }

        foreach (var inputs in InputsByClass(workflow, "RandomNoise"))
        {
            inputs["noise_seed"] = seed;
            applied = true;
        }

        if (steps.HasValue)
        {
            foreach (var inputs in InputsByClass(workflow, "Flux2Scheduler"))
            {
                inputs["steps"] = steps.Value;
                applied = true;
            }
        }

        if (!applied)
        {
            throw new InvalidOperationException("The workflow does not contain a sampler. Install the required models and replace the placeholder workflow.");
        }
    }

    public static void ApplyLoadImage(JsonObject workflow, string imageFileName)
    {
        var applied = false;

        foreach (var node in workflow.Select(pair => pair.Value).OfType<JsonObject>())
        {
            var classType = node["class_type"]?.GetValue<string>();

            if (classType is "EmptyImage")
            {
                node["class_type"] = "LoadImage";
                node["inputs"] = new JsonObject
                {
                    ["image"] = imageFileName
                };
                applied = true;
            }
            else if (classType is "LoadImage" && node["inputs"] is JsonObject inputs)
            {
                inputs["image"] = imageFileName;
                applied = true;
            }
        }

        if (!applied)
        {
            throw new InvalidOperationException("The workflow does not contain an input image node.");
        }
    }

    public static void ApplyLora(JsonObject workflow, string loraName, double strength)
    {
        var applied = false;

        foreach (var inputs in InputsByClass(workflow, "LoraLoader"))
        {
            inputs["lora_name"] = loraName;
            inputs["strength_model"] = strength;
            inputs["strength_clip"] = strength;
            applied = true;
        }

        if (!applied)
        {
            throw new InvalidOperationException("The workflow does not contain a LoRA loader.");
        }
    }

    public static void ApplyFluxModels(JsonObject workflow, string unetName, string clipName)
    {
        var unetApplied = false;
        var clipApplied = false;

        foreach (var inputs in InputsByClass(workflow, "UNETLoader"))
        {
            inputs["unet_name"] = unetName;
            unetApplied = true;
        }

        foreach (var inputs in InputsByClass(workflow, "CLIPLoader"))
        {
            inputs["clip_name"] = clipName;
            clipApplied = true;
        }

        if (!unetApplied || !clipApplied)
        {
            throw new InvalidOperationException("The workflow does not contain the required Flux model loaders.");
        }
    }

    public static void ApplyUpscaler(JsonObject workflow, string upscaler, int scale)
    {
        var modelLoaderApplied = false;
        var scaleApplied = false;

        foreach (var inputs in InputsByClass(workflow, "UpscaleModelLoader"))
        {
            inputs["model_name"] = upscaler;
            modelLoaderApplied = true;
        }

        foreach (var inputs in InputsByClass(workflow, "ImageScale"))
        {
            scaleApplied = true;

            if (inputs["width"]?.GetValue<int>() is var width && width > 0)
            {
                inputs["width"] = width / 2 * scale;
            }

            if (inputs["height"]?.GetValue<int>() is var height && height > 0)
            {
                inputs["height"] = height / 2 * scale;
            }
        }

        if (!modelLoaderApplied)
        {
            throw new InvalidOperationException("The workflow does not contain an upscale model loader.");
        }

        if (!scaleApplied)
        {
            throw new InvalidOperationException("The workflow does not contain a configurable image scale node.");
        }
    }

    private static IEnumerable<JsonObject> InputsByClass(JsonObject workflow, string classType)
    {
        return NodesByClass(workflow, classType)
            .Select(node => node["inputs"])
            .OfType<JsonObject>();
    }

    private static IEnumerable<JsonObject> NodesByClass(JsonObject workflow, string classType)
    {
        return workflow.Select(pair => pair.Value)
            .OfType<JsonObject>()
            .Where(node => node["class_type"]?.GetValue<string>() == classType);
    }
}
