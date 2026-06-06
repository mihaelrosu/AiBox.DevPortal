using AiBox.DevPortal.Models;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiBox.DevPortal.Services;

public sealed class ComfyUiService(HttpClient httpClient, IConfiguration configuration, IWebHostEnvironment environment) : IComfyUiService
{
    private const int MaxHistoryAttempts = 600;
    private static readonly TimeSpan HistoryPollDelay = TimeSpan.FromSeconds(1);

    public async Task<ServiceHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<ServiceHealth> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var url = httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost:8188";

        try
        {
            using var response = await httpClient.GetAsync("/", cancellationToken);
            return new ServiceHealth("ComfyUI", url, response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "Online" : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            return new ServiceHealth("ComfyUI", url, false, exception.Message);
        }
    }

    public async Task<IReadOnlyList<ComfyUiWorkflowFile>> GetWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowTemplatesAsync(cancellationToken);

        var folder = GetConfiguredPath("WorkflowFolder", "/app/comfyui/user/default/workflows");
        Directory.CreateDirectory(folder);

        return Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new ComfyUiWorkflowFile
                {
                    FileName = info.Name,
                    Modified = info.LastWriteTimeUtc,
                    SizeBytes = info.Length
                };
            })
            .OrderBy(file => file.FileName)
            .ToArray();
    }

    public async Task<string> GetWorkflowAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = GetWorkflowPath(fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Workflow file was not found.", fileName);
        }

        return await File.ReadAllTextAsync(path, cancellationToken);
    }

    public async Task SaveWorkflowAsync(string fileName, string json, CancellationToken cancellationToken = default)
    {
        _ = JsonNode.Parse(json) ?? throw new InvalidOperationException("Workflow JSON must contain a root object.");

        var path = GetWorkflowPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            var backupFolder = Path.Combine(GetDataFolder(), "workflow-overwrite-backups");
            Directory.CreateDirectory(backupFolder);
            var backupPath = Path.Combine(backupFolder, $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            File.Copy(path, backupPath, overwrite: false);
        }

        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task<string> BackupWorkflowsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWorkflowTemplatesAsync(cancellationToken);

        var workflowFolder = GetConfiguredPath("WorkflowFolder", "/app/comfyui/user/default/workflows");
        var backupFolder = Path.Combine(GetDataFolder(), "workflow-backups");
        Directory.CreateDirectory(backupFolder);

        var backupPath = Path.Combine(backupFolder, $"comfyui-workflows-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.zip");

        await Task.Run(() =>
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            ZipFile.CreateFromDirectory(workflowFolder, backupPath, CompressionLevel.Fastest, includeBaseDirectory: false);
        }, cancellationToken);

        return backupPath;
    }

    public async Task<ComfyUiQueueResult> QueuePromptAsync(string workflowJson, CancellationToken cancellationToken = default)
    {
        var workflow = JsonNode.Parse(workflowJson) as JsonObject
            ?? throw new InvalidOperationException("Workflow JSON must contain an object at the root.");

        ValidateApiWorkflow(workflow);

        using var response = await httpClient.PostAsJsonAsync("prompt", new { prompt = workflow }, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ComfyUI rejected the workflow: HTTP {(int)response.StatusCode} {responseText}");
        }

        var payload = JsonNode.Parse(responseText) as JsonObject;
        return new ComfyUiQueueResult
        {
            PromptId = payload?["prompt_id"]?.GetValue<string>() ?? string.Empty,
            Message = responseText
        };
    }

    public async Task<ComfyUiGenerationResult> QueuePromptAndWaitForImageAsync(string workflowJson, long seed, CancellationToken cancellationToken = default)
    {
        var queueResult = await QueuePromptAsync(workflowJson, cancellationToken);

        if (string.IsNullOrWhiteSpace(queueResult.PromptId))
        {
            throw new InvalidOperationException("ComfyUI did not return a prompt_id.");
        }

        var image = await WaitForImageAsync(queueResult.PromptId, cancellationToken);

        return new ComfyUiGenerationResult
        {
            PromptId = queueResult.PromptId,
            ImageUrl = BuildImageUrl(image),
            Seed = seed
        };
    }

    public async Task<string> SaveInputImageAsync(string fileName, Stream stream, CancellationToken cancellationToken = default)
    {
        var safeFileName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var inputFolder = GetConfiguredPath("InputFolder", "/app/comfyui/input");
        Directory.CreateDirectory(inputFolder);
        var path = Path.Combine(inputFolder, safeFileName);

        await using var output = File.Create(path);
        await stream.CopyToAsync(output, cancellationToken);
        return safeFileName;
    }

    public async Task<ComfyUiModelInventory> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        return new ComfyUiModelInventory
        {
            Checkpoints = await GetCheckpointsAsync(cancellationToken),
            Loras = await GetLorasAsync(cancellationToken),
            Upscalers = await GetUpscalersAsync(cancellationToken),
            ControlNetModels = await GetControlNetModelsAsync(cancellationToken),
            FluxFiles = GetFiles(GetConfiguredPath("FluxModelFolder", "/app/comfyui/models/unet"))
        };
    }

    public Task<IReadOnlyList<string>> GetCheckpointsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetFiles(GetConfiguredPath("CheckpointFolder", "/app/comfyui/models/checkpoints")));
    }

    public Task<IReadOnlyList<string>> GetLorasAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetFiles(GetConfiguredPath("LoraFolder", "/app/comfyui/models/loras")));
    }

    public Task<IReadOnlyList<string>> GetUpscalersAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetFiles(GetConfiguredPath("UpscalerFolder", "/app/comfyui/models/upscale_models")));
    }

    public Task<IReadOnlyList<string>> GetControlNetModelsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetFiles(GetConfiguredPath("ControlNetFolder", "/app/comfyui/models/controlnet")));
    }

    private async Task EnsureWorkflowTemplatesAsync(CancellationToken cancellationToken)
    {
        var sourceFolder = Path.Combine(environment.ContentRootPath, "Workflows", "ComfyUI");
        var targetFolder = GetConfiguredPath("WorkflowFolder", "/app/comfyui/user/default/workflows");

        if (!Directory.Exists(sourceFolder))
        {
            return;
        }

        Directory.CreateDirectory(targetFolder);

        foreach (var sourcePath in Directory.EnumerateFiles(sourceFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            var targetPath = Path.Combine(targetFolder, Path.GetFileName(sourcePath));

            if (File.Exists(targetPath))
            {
                continue;
            }

            await using var source = File.OpenRead(sourcePath);
            await using var target = File.Create(targetPath);
            await source.CopyToAsync(target, cancellationToken);
        }
    }

    private string GetWorkflowPath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Workflow file name is required.", nameof(fileName));
        }

        var safeFileName = Path.GetFileName(fileName);
        var folder = GetConfiguredPath("WorkflowFolder", "/app/comfyui/user/default/workflows");
        return Path.Combine(folder, safeFileName);
    }

    private async Task<ComfyImage> WaitForImageAsync(string promptId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxHistoryAttempts; attempt++)
        {
            var history = await httpClient.GetFromJsonAsync<JsonObject>($"history/{Uri.EscapeDataString(promptId)}", cancellationToken);
            var image = FindOutputImage(history, promptId);

            if (image is not null)
            {
                return image;
            }

            await Task.Delay(HistoryPollDelay, cancellationToken);
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
        var query = $"view?filename={Uri.EscapeDataString(image.Filename)}&type={Uri.EscapeDataString(image.Type)}";

        if (!string.IsNullOrWhiteSpace(image.Subfolder))
        {
            query += $"&subfolder={Uri.EscapeDataString(image.Subfolder)}";
        }

        return new Uri(baseUri, query).ToString();
    }

    private string GetConfiguredPath(string key, string fallback)
    {
        return configuration[$"AiBox:ComfyUI:{key}"] ?? fallback;
    }

    private string GetDataFolder()
    {
        return Path.Combine(environment.ContentRootPath, "Data");
    }

    private static IReadOnlyList<string> GetFiles(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name)
            .ToArray();
    }

    private static void ValidateApiWorkflow(JsonObject workflow)
    {
        foreach (var (nodeId, node) in workflow)
        {
            if (node is not JsonObject nodeObject ||
                nodeObject["class_type"] is not JsonValue ||
                nodeObject["inputs"] is not JsonObject)
            {
                throw new InvalidOperationException($"Workflow node '{nodeId}' is not a valid ComfyUI API-format node.");
            }

            var title = nodeObject["_meta"]?["title"]?.GetValue<string>();
            if (title?.Contains("placeholder", StringComparison.OrdinalIgnoreCase) == true ||
                title?.Contains("missing local", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException($"Workflow node '{nodeId}' is a placeholder: {title}. Install the required models and replace the workflow before generating.");
            }
        }
    }

    private sealed record ComfyImage(string Filename, string Subfolder, string Type);
}
