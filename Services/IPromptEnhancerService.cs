using AiBox.DevPortal.Models;

namespace AiBox.DevPortal.Services;

public interface IPromptEnhancerService
{
    Task<PromptEnhanceResult> ImproveSdxlPromptAsync(string positivePrompt, string? style = null);
    Task<PromptEnhanceResult> CreateSdxlNegativePromptAsync(string positivePrompt, string? style = null);
}
