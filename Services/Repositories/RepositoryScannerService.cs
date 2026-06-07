using AiBox.DevPortal.Models.Repositories;

namespace AiBox.DevPortal.Services.Repositories;

public sealed class RepositoryScannerService : IRepositoryScannerService
{
    private const int MaxDirectories = 250;
    private const int MaxFiles = 750;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        ".idea",
        ".vs",
        ".vscode",
        ".ssh",
        ".aws",
        ".azure",
        ".gnupg",
        ".kube",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "dist",
        "build",
        "coverage",
        "secrets"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".env.local",
        ".env.development",
        ".env.production",
        "credentials",
        "credentials.json",
        "secrets.json",
        "appsettings.secrets.json"
    };

    private static readonly string[] ExcludedFileSuffixes =
    [
        ".key",
        ".pem",
        ".pfx",
        ".p12",
        ".jks",
        ".keystore"
    ];

    public Task<RepositoryScanResult> ScanAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("A repository path is required.", nameof(repositoryPath));
        }

        var rootPath = Path.GetFullPath(repositoryPath.Trim());

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Repository path '{rootPath}' was not found.");
        }

        var directories = new List<string>();
        var files = new List<RepositoryFileSummary>();
        var pendingDirectories = new Queue<string>();
        var skippedDirectoryCount = 0;
        var skippedFileCount = 0;
        var truncated = false;

        pendingDirectories.Enqueue(rootPath);

        while (pendingDirectories.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Dequeue();

            foreach (var directory in EnumerateDirectories(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(directory);

                if (ExcludedDirectoryNames.Contains(name) || IsReparsePoint(directory))
                {
                    skippedDirectoryCount++;
                    continue;
                }

                if (directories.Count >= MaxDirectories)
                {
                    truncated = true;
                    break;
                }

                directories.Add(NormalizeRelativePath(rootPath, directory));
                pendingDirectories.Enqueue(directory);
            }

            if (truncated)
            {
                break;
            }

            foreach (var file in EnumerateFiles(currentDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsSecretFileName(Path.GetFileName(file)))
                {
                    skippedFileCount++;
                    continue;
                }

                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }

                files.Add(new RepositoryFileSummary
                {
                    RelativePath = NormalizeRelativePath(rootPath, file)
                });
            }
        }

        return Task.FromResult(new RepositoryScanResult
        {
            RepositoryPath = rootPath,
            Directories = directories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Files = files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            SkippedDirectoryCount = skippedDirectoryCount,
            SkippedFileCount = skippedFileCount,
            Truncated = truncated
        });
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool IsSecretFileName(string fileName)
    {
        if (ExcludedFileNames.Contains(fileName)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExcludedFileSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string rootPath, string path)
    {
        return Path.GetRelativePath(rootPath, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
