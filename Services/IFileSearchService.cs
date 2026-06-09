using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IFileSearchService
{
    Task<List<FileSearchItem>> SearchAsync(
        string rootDirectory,
        string searchText,
        string searchPattern = "*.*",
        bool includeSubdirectories = true,
        int maxResults = 25);
}
