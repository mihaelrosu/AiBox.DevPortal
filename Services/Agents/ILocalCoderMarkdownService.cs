using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public interface ILocalCoderMarkdownService
{
    string Export(LocalCoderTaskHistoryRecord record);
    LocalCoderTaskHistoryRecord Import(string markdown);
}
