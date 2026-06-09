using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class FileSearchService : IFileSearchService
{
    public Task<List<FileSearchItem>> SearchAsync(
        string rootDirectory,
        string searchText,
        string searchPattern = "*.*",
        bool includeSubdirectories = true,
        int maxResults = 25)
    {
        var results = new List<FileSearchItem>();

        if (maxResults <= 0)
        {
            return Task.FromResult(results);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathRooted(rootDirectory))
            {
                return Task.FromResult(results);
            }

            var rootPath = Path.GetFullPath(rootDirectory);

            if (!Directory.Exists(rootPath))
            {
                return Task.FromResult(results);
            }

            var normalizedSearch = (searchText ?? string.Empty).Trim();
            var comparison = StringComparison.OrdinalIgnoreCase;

            foreach (var filePath in EnumerateFilesSafely(rootPath, searchPattern, includeSubdirectories))
            {
                if (!TryCreateSearchItem(rootPath, filePath, out var item))
                {
                    continue;
                }

                if (!MatchesSearch(item, normalizedSearch, comparison))
                {
                    continue;
                }

                results.Add(item);
            }
        }
        catch
        {
            return Task.FromResult(results);
        }

        return Task.FromResult(
            results
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Take(maxResults)
                .ToList());
    }

    private static IEnumerable<string> EnumerateFilesSafely(string rootPath, string searchPattern, bool includeSubdirectories)
    {
        if (!includeSubdirectories)
        {
            foreach (var filePath in EnumerateFilesInDirectory(rootPath, searchPattern))
            {
                yield return filePath;
            }

            yield break;
        }

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var currentDirectory = stack.Pop();

            foreach (var filePath in EnumerateFilesInDirectory(currentDirectory, searchPattern))
            {
                yield return filePath;
            }

            foreach (var directoryPath in EnumerateDirectoriesInDirectory(currentDirectory))
            {
                stack.Push(directoryPath);
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesInDirectory(string directoryPath, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath, string.IsNullOrWhiteSpace(searchPattern) ? "*.*" : searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesInDirectory(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryCreateSearchItem(string rootPath, string filePath, out FileSearchItem item)
    {
        item = default!;

        try
        {
            var fileInfo = new FileInfo(filePath);
            var relativePath = Path.GetRelativePath(rootPath, filePath).Replace(Path.DirectorySeparatorChar, '/');

            item = new FileSearchItem
            {
                FileName = fileInfo.Name,
                FullPath = fileInfo.FullName,
                RelativePath = relativePath,
                Extension = fileInfo.Extension,
                SizeBytes = fileInfo.Exists ? fileInfo.Length : 0
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesSearch(FileSearchItem item, string searchText, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return item.FileName.Contains(searchText, comparison) ||
               item.RelativePath.Contains(searchText, comparison);
    }
}
