using System.Text.Json;

namespace AiBox.DevPortal.Services;

internal static class JsonFileStore
{
    public static async Task<List<T>> LoadListAsync<T>(
        string path,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default,
        Func<List<T>>? fallbackFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
        {
            return await LoadFallbackAsync(path, options, cancellationToken, fallbackFactory);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, options, cancellationToken) ?? [];
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return await LoadFallbackAsync(path, options, cancellationToken, fallbackFactory);
        }
    }

    public static async Task SaveListAsync<T>(
        string path,
        IReadOnlyList<T> items,
        JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, items, options, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static async Task<List<T>> LoadFallbackAsync<T>(
        string path,
        JsonSerializerOptions options,
        CancellationToken cancellationToken,
        Func<List<T>>? fallbackFactory)
    {
        if (fallbackFactory is null)
        {
            return [];
        }

        var fallback = fallbackFactory();
        await SaveListAsync(path, fallback, options, cancellationToken);
        return fallback;
    }
}
