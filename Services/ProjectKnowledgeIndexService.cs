using System.Text.Json;
using System.Text.RegularExpressions;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public sealed class ProjectKnowledgeIndexService : IProjectKnowledgeIndexService
{
    private const string IndexPath = "Data/project-knowledge-index.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] IgnoredDirectoryNames =
    [
        "bin",
        "obj",
        ".git",
        "node_modules"
    ];

    public async Task<ProjectKnowledgeIndex> BuildAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new ArgumentException("Project root is required.", nameof(projectRoot));
        }

        var fullProjectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(fullProjectRoot))
        {
            throw new DirectoryNotFoundException($"Project root was not found: {fullProjectRoot}");
        }

        var items = new List<ProjectKnowledgeItem>();
        foreach (var filePath in Directory.EnumerateFiles(fullProjectRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(fullProjectRoot, filePath).Replace('\\', '/');
            if (IsIgnoredPath(relativePath))
            {
                continue;
            }

            var extension = Path.GetExtension(relativePath);
            if (!IsSupportedExtension(extension))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            items.Add(new ProjectKnowledgeItem
            {
                RelativePath = relativePath,
                FileType = GetFileType(extension),
                Name = GetName(relativePath),
                Namespace = GetNamespace(relativePath, content),
                Kind = GetKind(relativePath, extension, content),
                Summary = GetSummary(relativePath, extension, content),
                LastModifiedUtc = File.GetLastWriteTimeUtc(filePath)
            });
        }

        var index = new ProjectKnowledgeIndex
        {
            ProjectPath = fullProjectRoot,
            RebuiltAtUtc = DateTime.UtcNow,
            Items = items
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        await SaveAsync(index, cancellationToken);
        return index;
    }

    public async Task<ProjectKnowledgeIndex?> GetLatestAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        if (!File.Exists(IndexPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(IndexPath);
        var index = await JsonSerializer.DeserializeAsync<ProjectKnowledgeIndex>(stream, JsonOptions, cancellationToken);
        if (index is null)
        {
            return null;
        }

        var normalizedRequestedRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        var normalizedIndexedRoot = Path.GetFullPath(index.ProjectPath).TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(normalizedRequestedRoot, normalizedIndexedRoot, StringComparison.OrdinalIgnoreCase)
            ? index
            : null;
    }

    private static async Task SaveAsync(ProjectKnowledgeIndex index, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!);

        await using var stream = File.Create(IndexPath);
        await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken);
    }

    private static bool IsSupportedExtension(string extension)
    {
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".razor", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return IgnoredDirectoryNames.Any(directory =>
            normalized.StartsWith($"{directory}/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains($"/{directory}/", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetFileType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "CSharp",
            ".razor" => "Razor",
            ".csproj" => "Project",
            ".json" => "Config",
            _ => "Unknown"
        };
    }

    private static string GetName(string relativePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        return string.IsNullOrWhiteSpace(fileName) ? relativePath : fileName;
    }

    private static string GetNamespace(string relativePath, string content)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(content, @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;?", RegexOptions.Multiline);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        if (extension.Equals(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(content, @"^\s*@namespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*$", RegexOptions.Multiline);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string GetKind(string relativePath, string extension, string content)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return GetCsKind(relativePath);
        }

        if (extension.Equals(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return content.Contains("@page", StringComparison.OrdinalIgnoreCase) ? "RazorPage" : "RazorComponent";
        }

        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? "Project"
            : extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? "Config"
                : "Unknown";
    }

    private static string GetCsKind(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);

        if (IsInFolder(normalized, "Tests") ||
            normalized.EndsWith(".Tests.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Test";
        }

        if (IsInFolder(normalized, "Controllers") ||
            fileName.EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Controller";
        }

        if (IsInFolder(normalized, "Models") ||
            fileName.EndsWith("Model.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Record.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("Entity.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Model";
        }

        if (IsInFolder(normalized, "Services") ||
            fileName.EndsWith("Service.cs", StringComparison.OrdinalIgnoreCase))
        {
            return "Service";
        }

        return "Unknown";
    }

    private static bool IsInFolder(string relativePath, string folderName)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith($"{folderName}/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"/{folderName}/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSummary(string relativePath, string extension, string content)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".razor", StringComparison.OrdinalIgnoreCase))
        {
            var xmlSummary = ExtractXmlSummary(content);
            if (!string.IsNullOrWhiteSpace(xmlSummary))
            {
                return xmlSummary;
            }
        }

        return string.Empty;
    }

    private static string ExtractXmlSummary(string content)
    {
        var match = Regex.Match(
            content,
            @"///\s*<summary>\s*(?<summary>.*?)\s*///\s*</summary>",
            RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return string.Empty;
        }

        var summary = match.Groups["summary"].Value
            .Replace("///", string.Empty, StringComparison.Ordinal)
            .Trim();

        var lines = summary
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => line.Trim().TrimStart('/').Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return string.Join(" ", lines);
    }
}
