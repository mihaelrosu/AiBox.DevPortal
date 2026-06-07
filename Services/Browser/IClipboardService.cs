namespace AiBox.DevPortal.Services.Browser;

public interface IClipboardService
{
    ValueTask CopyTextAsync(string text);
}
