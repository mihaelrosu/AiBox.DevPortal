namespace AiBox.DevPortal.Models;

public sealed class LocalCoderRequest
{
    public string ProjectPath { get; set; } = string.Empty;
    public string Model { get; set; } = "qwen2.5-coder:7b";
    public string Task { get; set; } = string.Empty;
    public List<LocalCoderFileContext> FileContexts { get; set; } = [];
    public PatchScopeMode AllowedPatchScope { get; set; } = PatchScopeMode.ContextFilesOnly;
    public List<string> AllowedPatchFolders { get; set; } = [];
    public List<string> AllowedCreateFolders { get; set; } = [];
}
