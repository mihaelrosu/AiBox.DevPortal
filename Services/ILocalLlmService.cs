namespace AiBox.DevPortal.Services;

public interface ILocalLlmService
{
    Task<string> GenerateAsync(string model, string prompt, CancellationToken cancellationToken);
}
