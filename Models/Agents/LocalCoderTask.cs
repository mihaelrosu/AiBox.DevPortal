namespace AiBox.DevPortal.Models.Agents;

public sealed class LocalCoderTask
{
    public string HistoryId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string AllowedPathsText { get; set; } = string.Empty;
    public string ForbiddenPathsText { get; set; } = string.Empty;
    public List<string> SelectedFilePaths { get; set; } = [];
    public bool RequireApprovalBeforeApply { get; set; }
    public string BuildCommand { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public LocalCoderTaskStatus Status { get; set; } = LocalCoderTaskStatus.Draft;
}
