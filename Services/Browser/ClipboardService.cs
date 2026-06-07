using Microsoft.JSInterop;

namespace AiBox.DevPortal.Services.Browser;

public sealed class ClipboardService(IJSRuntime jsRuntime) : IClipboardService
{
    public ValueTask CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Clipboard text is required.", nameof(text));
        }

        return jsRuntime.InvokeVoidAsync("aiBoxCopyToClipboard", text);
    }
}
