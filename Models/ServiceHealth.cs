namespace AiBox.DevPortal.Models;

public sealed record ServiceHealth(string Name, string Url, bool IsAvailable, string Status);
