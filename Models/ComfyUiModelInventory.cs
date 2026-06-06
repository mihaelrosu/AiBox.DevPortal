namespace AiBox.DevPortal.Models;

public sealed class ComfyUiModelInventory
{
    public IReadOnlyList<string> Checkpoints { get; set; } = [];
    public IReadOnlyList<string> Loras { get; set; } = [];
    public IReadOnlyList<string> Upscalers { get; set; } = [];
    public IReadOnlyList<string> ControlNetModels { get; set; } = [];
    public IReadOnlyList<string> FluxFiles { get; set; } = [];
}
