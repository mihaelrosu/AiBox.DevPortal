using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class LocalCoderMarkdownService : ILocalCoderMarkdownService
{
    private const int MaxImportCharacters = 2 * 1024 * 1024;
    private const string RecordStartMarker = "<!-- LOCAL_CODER_RECORD_START -->";
    private const string RecordEndMarker = "<!-- LOCAL_CODER_RECORD_END -->";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Export(LocalCoderTaskHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.Task);

        var title = string.IsNullOrWhiteSpace(record.Task.Title) ? "Untitled task" : record.Task.Title.Trim();
        var markdown = new StringBuilder();
        markdown.AppendLine($"# Local Coder Task: {title}");
        markdown.AppendLine();
        markdown.AppendLine("## Task");
        markdown.AppendLine();
        markdown.AppendLine($"- Repository: `{record.Task.RepositoryPath}`");
        markdown.AppendLine($"- Model: `{record.Task.Model}`");
        markdown.AppendLine($"- Status: `{record.Task.Status}`");
        markdown.AppendLine($"- Build command: `{record.Task.BuildCommand}`");
        markdown.AppendLine($"- Require approval before apply: `{record.Task.RequireApprovalBeforeApply}`");
        markdown.AppendLine();
        AppendSection(markdown, "Instructions", record.Task.Instructions);
        AppendSection(markdown, "Allowed Paths", record.Task.AllowedPathsText);
        AppendSection(markdown, "Forbidden Paths", record.Task.ForbiddenPathsText);
        AppendListSection(markdown, "Selected Context Files", record.Task.SelectedFilePaths ?? []);
        AppendSection(markdown, "Plan", record.PlanText);
        AppendSection(markdown, "Diff", record.DiffText);
        AppendSection(markdown, "Build Output", record.BuildOutput);
        AppendSection(markdown, "Review", record.ReviewText);
        markdown.AppendLine("## Portable Record");
        markdown.AppendLine();
        markdown.AppendLine("The JSON block below is used when importing this Markdown back into AiBox.DevPortal.");
        markdown.AppendLine();
        markdown.AppendLine(RecordStartMarker);
        markdown.AppendLine("```json");
        markdown.AppendLine(JsonSerializer.Serialize(record, JsonOptions));
        markdown.AppendLine("```");
        markdown.AppendLine(RecordEndMarker);
        return markdown.ToString();
    }

    public LocalCoderTaskHistoryRecord Import(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new ArgumentException("Markdown content is required.", nameof(markdown));
        }

        if (markdown.Length > MaxImportCharacters)
        {
            throw new FormatException($"Markdown import exceeds the {MaxImportCharacters / 1024 / 1024} MB text limit.");
        }

        var start = markdown.IndexOf(RecordStartMarker, StringComparison.Ordinal);
        var end = markdown.LastIndexOf(RecordEndMarker, StringComparison.Ordinal);

        if (start < 0 || end <= start)
        {
            throw new FormatException("The Markdown does not contain a Local Coder portable record.");
        }

        var block = markdown[(start + RecordStartMarker.Length)..end].Trim();

        if (block.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            block = block[7..].TrimStart();
        }

        if (block.EndsWith("```", StringComparison.Ordinal))
        {
            block = block[..^3].TrimEnd();
        }

        var record = JsonSerializer.Deserialize<LocalCoderTaskHistoryRecord>(block, JsonOptions)
            ?? throw new FormatException("The Local Coder portable record is empty.");

        record.Task ??= new LocalCoderTask();
        record.Id = string.Empty;
        record.Task.HistoryId = string.Empty;
        record.Task.Title ??= string.Empty;
        record.Task.RepositoryPath ??= string.Empty;
        record.Task.Model ??= string.Empty;
        record.Task.Instructions ??= string.Empty;
        record.Task.AllowedPathsText ??= string.Empty;
        record.Task.ForbiddenPathsText ??= string.Empty;
        record.Task.SelectedFilePaths ??= [];
        record.Task.BuildCommand ??= string.Empty;
        record.PlanText ??= string.Empty;
        record.DiffText ??= string.Empty;
        record.BuildOutput ??= string.Empty;
        record.ReviewText ??= string.Empty;
        record.CreatedAt = DateTime.UtcNow;
        record.UpdatedAt = DateTime.UtcNow;
        return record;
    }

    private static void AppendSection(StringBuilder markdown, string title, string content)
    {
        markdown.AppendLine($"## {title}");
        markdown.AppendLine();
        markdown.AppendLine(string.IsNullOrWhiteSpace(content) ? "_None_" : content.Trim());
        markdown.AppendLine();
    }

    private static void AppendListSection(StringBuilder markdown, string title, IReadOnlyList<string> items)
    {
        markdown.AppendLine($"## {title}");
        markdown.AppendLine();

        if (items.Count == 0)
        {
            markdown.AppendLine("_None_");
        }
        else
        {
            foreach (var item in items)
            {
                markdown.AppendLine($"- `{item}`");
            }
        }

        markdown.AppendLine();
    }
}
