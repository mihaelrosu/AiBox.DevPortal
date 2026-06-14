using System.Text;
using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

/// <summary>
/// Manages agent instructions and builds context based on file paths.
/// </summary>
public sealed class AgentInstructionService(IWebHostEnvironment environment)
{
    /// <summary>
    /// Builds context for agent instructions asynchronously.
    /// </summary>
    /// <param name="filePaths">List of file paths to include in the context.</param>
    /// <returns>A task that represents the asynchronous operation, containing an AgentInstructionContext.</returns>
    public async Task<AgentInstructionContext> BuildContextAsync(IEnumerable<string> filePaths)
    {
        return await BuildContextAsync(GetRootPath(), filePaths);
    }

    /// <summary>
    /// Builds context for agent instructions asynchronously with a specified project root.
    /// </summary>
    /// <param name="projectRoot">Project root directory.</param>
    /// <param name="filePaths">List of file paths to include in the context.</param>
    /// <returns>A task that represents the asynchronous operation, containing an AgentInstructionContext.</returns>
    public async Task<AgentInstructionContext> BuildContextAsync(string projectRoot, IEnumerable<string> filePaths)
    {
        var rootPath = GetRootPath(projectRoot);
        var relevantFiles = await FindRelevantAgentFilesAsync(rootPath, filePaths);
        var files = new List<AgentInstructionFile>(relevantFiles.Count);

        foreach (var relativePath in relevantFiles)
        {
            var fullPath = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var content = await File.ReadAllTextAsync(fullPath);

            files.Add(new AgentInstructionFile
            {
                RelativePath = relativePath,
                Content = content
            });
        }

        return new AgentInstructionContext
        {
            RelevantAgentFiles = relevantFiles,
            Files = files,
            CombinedText = BuildCombinedText(files)
        };
    }

    /// <summary>
    /// Finds relevant agent files based on the provided file paths.
    /// </summary>
    /// <param name="filePaths">List of file paths to consider.</param>
    /// <returns>A task that represents the asynchronous operation, containing a list of relevant agent file paths.</returns>
    public Task<List<string>> FindRelevantAgentFilesAsync(IEnumerable<string> filePaths)
    {
        return FindRelevantAgentFilesAsync(GetRootPath(), filePaths);
    }

    /// <summary>
    /// Finds relevant agent files based on the provided file paths and project root.
    /// </summary>
    /// <param name="projectRoot">Project root directory.</param>
    /// <param name="filePaths">List of file paths to consider.</param>
    /// <returns>A task that represents the asynchronous operation, containing a list of relevant agent file paths.</returns>
    public Task<List<string>> FindRelevantAgentFilesAsync(string projectRoot, IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var rootPath = GetRootPath(projectRoot);
        var candidateFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalizedRelativePath = NormalizeRelativePath(rootPath, filePath);
            foreach (var candidate in EnumerateAgentFileCandidates(normalizedRelativePath))
            {
                var fullPath = Path.Combine(rootPath, candidate.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    candidateFiles.Add(candidate);
                }
            }
        }

        var ordered = candidateFiles
            .OrderBy(path => GetPathDepth(path), Comparer<int>.Default)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(ordered);
    }

    /// <summary>
    /// Retrieves the application content root path.
    /// </summary>
    /// <returns>The full path of the application content root.</returns>
    private string GetRootPath()
    {
        return GetRootPath(environment.ContentRootPath);
    }

    /// <summary>
    /// Retrieves the absolute path of the specified root path.
    /// </summary>
    /// <param name="rootPath">The project root directory.</param>
    /// <returns>The full path of the specified root path.</returns>
    private static string GetRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new InvalidOperationException("The application content root was not configured.");
        }

        return Path.GetFullPath(rootPath);
    }

    /// <summary>
    /// Normalizes the relative path to ensure it is within the project root.
    /// </summary>
    /// <param name="rootPath">The project root directory.</param>
    /// <param name="filePath">The file path to normalize.</param>
    /// <returns>The normalized relative path.</returns>
    private static string NormalizeRelativePath(string rootPath, string filePath)
    {
        var trimmedPath = filePath.Trim();
        var fullPath = Path.GetFullPath(Path.IsPathRooted(trimmedPath) ? trimmedPath : Path.Combine(rootPath, trimmedPath));
        var normalizedRootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(normalizedRootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File path '{trimmedPath}' is outside the project root.");
        }

        return Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Enumerates agent file candidates based on the provided relative file path.
    /// </summary>
    /// <param name="relativeFilePath">The relative file path.</param>
    /// <returns>An enumerable list of candidate agent file paths.</returns>
    private static IEnumerable<string> EnumerateAgentFileCandidates(string relativeFilePath)
    {
        yield return "AGENTS.md";

        var directoryPath = Path.GetDirectoryName(relativeFilePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            yield break;
        }

        var segments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            current.Add(segment);
            yield return string.Join('/', current) + "/AGENTS.md";
        }
    }

    /// <summary>
    /// Retrieves the depth of a path by counting the segments.
    /// </summary>
    /// <param name="path">The path to analyze.</param>
    /// <returns>The depth of the path.</returns>
    private static int GetPathDepth(string path)
    {
        return path.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            ? 0
            : path.Count(character => character == '/');
    }

    /// <summary>
    /// Builds a combined text representation of agent instruction files.
    /// </summary>
    /// <param name="files">List of agent instruction files.</param>
    /// <returns>The combined text representation.</returns>
    private static string BuildCombinedText(IReadOnlyList<AgentInstructionFile> files)
    {
        if (files.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var file in files)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.AppendLine($"FILE: {file.RelativePath}");
            builder.AppendLine(file.Content.TrimEnd());
        }

        return builder.ToString();
    }
}
