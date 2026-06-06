using System.Text.Json;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class GeneratedImageHistoryService(IWebHostEnvironment environment) : IGeneratedImageHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly SemaphoreSlim FileLock = new(1, 1);

    public async Task<IReadOnlyList<GeneratedImage>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var images = await LoadAsync(cancellationToken);
            return images
                .OrderByDescending(image => image.Created)
                .ToArray();
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<GeneratedImage?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var images = await LoadAsync(cancellationToken);
            return images.FirstOrDefault(image => image.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            FileLock.Release();
        }
    }

    public async Task<GeneratedImage> SaveAsync(SdxlTextToImageResult result, CancellationToken cancellationToken = default)
    {
        var image = new GeneratedImage
        {
            Id = Guid.NewGuid().ToString("N"),
            Created = DateTimeOffset.UtcNow,
            Seed = result.Seed,
            Prompt = result.PositivePrompt,
            NegativePrompt = result.NegativePrompt,
            Model = result.Model,
            Width = result.Width,
            Height = result.Height,
            Steps = result.Steps,
            CFG = result.Cfg,
            ImageUrl = result.ImageUrl
        };

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var images = await LoadAsync(cancellationToken);
            images.Add(image);
            await SaveAllAsync(images, cancellationToken);
            return image;
        }
        finally
        {
            FileLock.Release();
        }
    }

    private async Task<List<GeneratedImage>> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();

        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<GeneratedImage>>(stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task SaveAllAsync(List<GeneratedImage> images, CancellationToken cancellationToken)
    {
        var path = GetHistoryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, images, JsonOptions, cancellationToken);
    }

    private string GetHistoryPath()
    {
        return Path.Combine(environment.ContentRootPath, "Data", "generated-images.json");
    }
}
