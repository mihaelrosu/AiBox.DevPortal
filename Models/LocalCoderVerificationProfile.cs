namespace AiBox.DevPortal.Models;

public sealed class LocalCoderVerificationProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public IReadOnlyList<string> Commands { get; set; } = [];
}
