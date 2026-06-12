using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public static class PatchIntentGuard
{
    public static void ThrowIfBlocking(LocalCoderPatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var validation = preview.IntentValidation ?? (preview.Intent is null ? null : PatchIntentService.Evaluate(preview.Intent, preview));
        if (validation?.IsBlocking == true)
        {
            throw new InvalidOperationException(BuildBlockingMessage(validation));
        }
    }

    public static void ThrowIfBlocking(PatchPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var validation = package.IntentValidation ?? (package.Intent is null ? null : PatchIntentService.Evaluate(package.Intent, package));
        if (validation?.IsBlocking == true)
        {
            throw new InvalidOperationException(BuildBlockingMessage(validation));
        }
    }

    public static string BuildBlockingMessage(PatchIntentValidation validation)
    {
        var reasons = validation.Reasons?.Where(reason => !string.IsNullOrWhiteSpace(reason)).ToArray() ?? [];
        return reasons.Length > 0
            ? string.Join(" ", reasons)
            : "Patch intent validation failed.";
    }
}
