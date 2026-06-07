using System.Text;
using AiBox.DevPortal.Models.Repositories;

namespace AiBox.DevPortal.Services.Repositories;

public sealed class RepositoryFileContextService : IRepositoryFileContextService
{
    private const int MaxFileBytes = 32 * 1024;
    private const int MaxTotalBytes = 128 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".razor",
        ".cshtml",
        ".csproj",
        ".sln",
        ".props",
        ".targets",
        ".json",
        ".xml",
        ".md",
        ".txt",
        ".css",
        ".scss",
        ".html",
        ".js",
        ".jsx",
        ".ts",
        ".tsx",
        ".yml",
        ".yaml"
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".svn",
        ".hg",
        ".ssh",
        ".aws",
        ".azure",
        ".gnupg",
        ".kube",
        "bin",
        "obj",
        "node_modules",
        "packages",
        "secrets"
    };

    private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env",
        ".env.local",
        ".env.development",
        ".env.production",
        "appsettings.production.json",
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

    public async Task<IReadOnlyList<RepositoryFileContent>> ReadAsync(
        string repositoryPath,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("A repository path is required.", nameof(repositoryPath));
        }

        ArgumentNullException.ThrowIfNull(relativePaths);

        var rootPath = Path.GetFullPath(repositoryPath.Trim());

        if (!Directory.Exists(rootPath))
        {
            throw new DirectoryNotFoundException($"Repository path '{rootPath}' was not found.");
        }

        var contents = new List<RepositoryFileContent>();
        var totalBytes = 0;

        foreach (var relativePath in relativePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRelativePath = ValidateRelativePath(rootPath, relativePath);
            var fullPath = Path.GetFullPath(Path.Combine(rootPath, normalizedRelativePath));
            ValidatePathSegments(rootPath, normalizedRelativePath);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Selected context file '{normalizedRelativePath}' was not found.", normalizedRelativePath);
            }

            if (File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Selected context file '{normalizedRelativePath}' cannot be a symbolic link.");
            }

            var fileLength = new FileInfo(fullPath).Length;

            if (fileLength > MaxFileBytes)
            {
                throw new InvalidOperationException($"Selected context file '{normalizedRelativePath}' exceeds the {MaxFileBytes / 1024} KB limit.");
            }

            if (totalBytes + fileLength > MaxTotalBytes)
            {
                throw new InvalidOperationException($"Selected file context exceeds the {MaxTotalBytes / 1024} KB total limit.");
            }

            var bytes = await ReadLimitedBytesAsync(fullPath, normalizedRelativePath, cancellationToken);
            var content = DecodeText(normalizedRelativePath, bytes);
            totalBytes += bytes.Length;
            contents.Add(new RepositoryFileContent
            {
                RelativePath = normalizedRelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                Content = content,
                CharacterCount = content.Length
            });
        }

        return contents;
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(
        string fullPath,
        string relativePath,
        CancellationToken cancellationToken)
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
            var read = await stream.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaxFileBytes)
            {
                throw new InvalidOperationException($"Selected context file '{relativePath}' exceeds the {MaxFileBytes / 1024} KB limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string ValidateRelativePath(string rootPath, string relativePath)
    {
        var trimmedPath = relativePath.Trim();

        if (Path.IsPathRooted(trimmedPath))
        {
            throw new InvalidOperationException("Selected context files must use repository-relative paths.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(rootPath, trimmedPath));
        var rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Selected context file '{trimmedPath}' is outside the repository.");
        }

        var fileName = Path.GetFileName(fullPath);

        if (IsSecretFileName(fileName))
        {
            throw new InvalidOperationException($"Selected context file '{trimmedPath}' is blocked because it may contain secrets or production settings.");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(fileName)))
        {
            throw new InvalidOperationException($"Selected context file '{trimmedPath}' is not an allowed text source file.");
        }

        return Path.GetRelativePath(rootPath, fullPath);
    }

    private static void ValidatePathSegments(string rootPath, string relativePath)
    {
        var currentPath = rootPath;
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments[..^1])
        {
            if (ExcludedDirectoryNames.Contains(segment))
            {
                throw new InvalidOperationException($"Selected context path '{relativePath}' contains a blocked directory.");
            }

            currentPath = Path.Combine(currentPath, segment);

            if (Directory.Exists(currentPath) && File.GetAttributes(currentPath).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"Selected context path '{relativePath}' cannot traverse a symbolic link.");
            }
        }
    }

    private static bool IsSecretFileName(string fileName)
    {
        if (ExcludedFileNames.Contains(fileName)
            || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("appsettings.production.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ExcludedFileSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static string DecodeText(string relativePath, byte[] bytes)
    {
        if (bytes.Contains((byte)0))
        {
            throw new InvalidOperationException($"Selected context file '{relativePath}' appears to be binary.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"Selected context file '{relativePath}' is not valid UTF-8 text.");
        }
    }
}
