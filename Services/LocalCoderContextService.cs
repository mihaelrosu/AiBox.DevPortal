using System.Text;
using System.Security.Cryptography;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class LocalCoderContextService : ILocalCoderContextService
{
    internal const long MaxFileBytes = 200 * 1024;
    internal const long MaxTotalBytes = 500 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".razor", ".json", ".yml", ".yaml", ".css", ".html", ".js", ".md", ".csproj", ".sln", ".props", ".targets"
    };

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<LocalCoderPresetPreview> PreviewPresetAsync(
        string projectRoot,
        string currentPagePath,
        LocalCoderContextPreset preset,
        IReadOnlyList<string> selectedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPagePath);
        ArgumentNullException.ThrowIfNull(selectedPaths);

        var rootPath = Path.GetFullPath(projectRoot);
        var normalizedPagePath = ValidateAndNormalizePath(rootPath, currentPagePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        if (!Path.GetExtension(normalizedPagePath).Equals(".razor", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Context presets require a selected Razor page.");
        }

        var featureName = Path.GetFileNameWithoutExtension(normalizedPagePath);
        var includeComponents = preset is LocalCoderContextPreset.PageAndComponents or LocalCoderContextPreset.FullFeatureContext;
        var includeServices = preset is LocalCoderContextPreset.PageAndServices or LocalCoderContextPreset.FullFeatureContext;
        var includeModels = preset is LocalCoderContextPreset.FullFeatureContext;
        var componentPrefix = $"Components/{featureName}/";

        var candidatePaths = EnumeratePresetCandidates(rootPath)
            .Where(relativePath =>
                relativePath.Equals(normalizedPagePath, StringComparison.OrdinalIgnoreCase)
                || includeComponents
                && relativePath.StartsWith(componentPrefix, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(relativePath).Equals(".razor", StringComparison.OrdinalIgnoreCase)
                || includeServices
                && relativePath.StartsWith("Services/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(relativePath).StartsWith(featureName, StringComparison.OrdinalIgnoreCase)
                || includeModels
                && relativePath.StartsWith("Models/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(relativePath).StartsWith(featureName, StringComparison.OrdinalIgnoreCase))
            .Append(normalizedPagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Equals(normalizedPagePath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedPathSet = selectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long totalBytes = selectedPaths
            .Select(path => TryGetFileLength(rootPath, path))
            .Sum();
        var previewFiles = new List<LocalCoderPresetPreviewFile>();

        foreach (var relativePath in candidatePaths)
        {
            var previewFile = await EvaluatePresetCandidateAsync(rootPath, relativePath, selectedPathSet, totalBytes);

            if (previewFile.Status == LocalCoderPresetFileStatus.Add)
            {
                totalBytes += previewFile.File.SizeBytes;
            }

            previewFiles.Add(previewFile);
        }

        return new LocalCoderPresetPreview { Files = previewFiles };
    }

    public async Task<IReadOnlyList<LocalCoderFileContext>> LoadAsync(
        string projectRoot,
        IReadOnlyList<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var rootPath = Path.GetFullPath(projectRoot);
        var contexts = new List<LocalCoderFileContext>();
        long totalBytes = 0;

        foreach (var relativePath in relativePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedRelativePath = ValidateAndNormalizePath(rootPath, relativePath);
            var fullPath = Path.Combine(rootPath, normalizedRelativePath);
            ValidateNoSymbolicLinks(rootPath, normalizedRelativePath);
            var fileLength = new FileInfo(fullPath).Length;

            if (fileLength > MaxFileBytes)
            {
                throw new InvalidOperationException(
                    $"File '{normalizedRelativePath}' exceeds the {MaxFileBytes / 1024} KB context limit.");
            }

            if (totalBytes + fileLength > MaxTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Selected file context exceeds the {MaxTotalBytes / 1024} KB total limit. Remove one or more files.");
            }

            var bytes = await ReadFileAsync(fullPath, normalizedRelativePath);

            if (totalBytes + bytes.Length > MaxTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Selected file context exceeds the {MaxTotalBytes / 1024} KB total limit. Remove one or more files.");
            }

            var content = DecodeText(bytes, normalizedRelativePath);
            totalBytes += bytes.Length;

            contexts.Add(new LocalCoderFileContext
            {
                RelativePath = normalizedRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                FullPath = fullPath,
                Content = content,
                IsGeneratedFile = IsGeneratedPath(normalizedRelativePath)
            });
        }

        return contexts;
    }

    public async Task<LocalCoderContextRestoreResult> RestoreAsync(
        string projectRoot,
        IReadOnlyList<LocalCoderHistoryContextFile> contextFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(contextFiles);

        var rootPath = Path.GetFullPath(projectRoot);
        var restoredContexts = new List<LocalCoderFileContext>();
        var results = new List<LocalCoderContextRestoreFile>();
        long totalBytes = 0;

        foreach (var snapshot in contextFiles)
        {
            var result = new LocalCoderContextRestoreFile { RelativePath = snapshot.RelativePath };
            results.Add(result);

            try
            {
                var normalizedPath = ValidateAndNormalizePath(rootPath, snapshot.RelativePath);
                var fullPath = Path.Combine(rootPath, normalizedPath);

                if (IsGeneratedPath(normalizedPath))
                {
                    result.SkipReason = "ignored folder";
                    continue;
                }

                ValidateNoSymbolicLinks(rootPath, normalizedPath);
                var fileLength = new FileInfo(fullPath).Length;

                if (fileLength > MaxFileBytes)
                {
                    result.SkipReason = "too large";
                    continue;
                }

                if (totalBytes + fileLength > MaxTotalBytes)
                {
                    result.SkipReason = "total context limit";
                    continue;
                }

                var bytes = await ReadFileAsync(fullPath, normalizedPath);
                var content = DecodeText(bytes, normalizedPath);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
                totalBytes += bytes.Length;

                restoredContexts.Add(new LocalCoderFileContext
                {
                    RelativePath = normalizedPath.Replace(Path.DirectorySeparatorChar, '/'),
                    FullPath = fullPath,
                    Content = content
                });
                result.Restored = true;
                result.ModifiedSinceRun = !hash.Equals(snapshot.ContentHash, StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException exception)
            {
                result.SkipReason = RestoreSkipReason(exception.Message);
            }
            catch (Exception)
            {
                result.SkipReason = "file could not be loaded";
            }
        }

        return new LocalCoderContextRestoreResult
        {
            Files = results,
            RestoredContexts = restoredContexts
        };
    }

    private static string RestoreSkipReason(string message)
    {
        if (message.Contains("outside", StringComparison.OrdinalIgnoreCase)
            || message.Contains("relative to the selected project", StringComparison.OrdinalIgnoreCase))
        {
            return "outside project root";
        }

        if (message.Contains("binary", StringComparison.OrdinalIgnoreCase)
            || message.Contains("supported text", StringComparison.OrdinalIgnoreCase))
        {
            return "binary file";
        }

        if (message.Contains("200 KB", StringComparison.OrdinalIgnoreCase))
        {
            return "too large";
        }

        if (message.Contains("no longer exists", StringComparison.OrdinalIgnoreCase))
        {
            return "file not found";
        }

        return "file could not be loaded";
    }

    private static bool IsGeneratedPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var pathSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return pathSegments.Any(segment =>
                   segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                   || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
               || normalizedPath.StartsWith("wwwroot/lib/", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumeratePresetCandidates(string rootPath)
    {
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentDirectory = stack.Pop();

            IEnumerable<string> files;
            IEnumerable<string> directories;

            try
            {
                files = Directory.EnumerateFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly);
                directories = Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var filePath in files)
            {
                yield return Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/');
            }

            foreach (var directoryPath in directories)
            {
                var relativePath = Path.GetRelativePath(rootPath, directoryPath);

                if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (IsGeneratedPath(relativePath))
                {
                    foreach (var ignoredFilePath in EnumerateFilesSafely(directoryPath))
                    {
                        yield return Path.GetRelativePath(rootPath, ignoredFilePath).Replace(Path.DirectorySeparatorChar, '/');
                    }
                }
                else
                {
                    stack.Push(directoryPath);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafely(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return [];
        }
    }

    private static async Task<LocalCoderPresetPreviewFile> EvaluatePresetCandidateAsync(
        string rootPath,
        string relativePath,
        IReadOnlySet<string> selectedPaths,
        long totalBytes)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var file = new FileSearchItem
        {
            FileName = Path.GetFileName(relativePath),
            FullPath = fullPath,
            RelativePath = relativePath,
            Extension = Path.GetExtension(relativePath),
            SizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0
        };

        if (!IsInsideRoot(rootPath, fullPath))
        {
            return Skipped(file, "outside project root");
        }

        if (selectedPaths.Contains(relativePath))
        {
            return new LocalCoderPresetPreviewFile
            {
                File = file,
                Status = LocalCoderPresetFileStatus.AlreadySelected,
                SkipReason = "already selected"
            };
        }

        if (IsGeneratedPath(relativePath))
        {
            return Skipped(file, "ignored folder");
        }

        if (!AllowedExtensions.Contains(file.Extension))
        {
            return Skipped(file, "binary file");
        }

        if (file.SizeBytes > MaxFileBytes)
        {
            return Skipped(file, "too large");
        }

        if (totalBytes + file.SizeBytes > MaxTotalBytes)
        {
            return Skipped(file, "total context limit");
        }

        try
        {
            DecodeText(await ReadFileAsync(fullPath, relativePath), relativePath);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("binary", StringComparison.OrdinalIgnoreCase))
        {
            return Skipped(file, "binary file");
        }

        return new LocalCoderPresetPreviewFile { File = file, Status = LocalCoderPresetFileStatus.Add };
    }

    private static LocalCoderPresetPreviewFile Skipped(FileSearchItem file, string reason) =>
        new() { File = file, Status = LocalCoderPresetFileStatus.Skipped, SkipReason = reason };

    private static long TryGetFileLength(string rootPath, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        return IsInsideRoot(rootPath, fullPath) && File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0;
    }

    private static bool IsInsideRoot(string rootPath, string fullPath)
    {
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, StringComparison.Ordinal);
    }

    private static string ValidateAndNormalizePath(string rootPath, string relativePath)
    {
        var trimmed = relativePath.Trim();

        if (Path.IsPathRooted(trimmed) || trimmed.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Selected file paths must be relative to the selected project.");
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"File '{trimmed}' is outside the selected project root.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, trimmed));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"File '{trimmed}' is outside the selected project root.");
        }

        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"File '{trimmed}' could not be loaded because it no longer exists.");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)))
        {
            throw new InvalidOperationException($"File '{trimmed}' is not a supported text source file.");
        }

        return Path.GetRelativePath(rootPath, fullPath);
    }

    private static void ValidateNoSymbolicLinks(string rootPath, string relativePath)
    {
        var currentPath = rootPath;

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);

            if (File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(
                    $"File '{relativePath}' cannot be loaded through a symbolic link outside the selected project root.");
            }
        }
    }

    private static async Task<byte[]> ReadFileAsync(string fullPath, string relativePath)
    {
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];

            while (true)
            {
                var read = await stream.ReadAsync(chunk);

                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > MaxFileBytes)
                {
                    throw new InvalidOperationException(
                        $"File '{relativePath}' exceeds the {MaxFileBytes / 1024} KB context limit.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"File '{relativePath}' could not be loaded. Check that it still exists and is readable.",
                exception);
        }
    }

    private static string DecodeText(byte[] bytes, string relativePath)
    {
        if (bytes.Contains((byte)0))
        {
            throw new InvalidOperationException($"File '{relativePath}' appears to be binary and cannot be used as context.");
        }

        try
        {
            var content = StrictUtf8.GetString(bytes);
            return content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"File '{relativePath}' appears to be binary and cannot be used as context.");
        }
    }
}
