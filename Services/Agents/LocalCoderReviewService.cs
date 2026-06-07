using AiBox.DevPortal.Models.Agents;

namespace AiBox.DevPortal.Services.Agents;

public sealed class LocalCoderReviewService(
    IOllamaService ollamaService) : ILocalCoderReviewService
{
    private const int MaxSectionCharacters = 96 * 1024;

    public async Task<LocalCoderResult> ReviewAsync(
        LocalCoderTask task,
        string planText,
        string patchText,
        LocalCoderPatchValidationResult patchValidation,
        string buildOutput,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        ArgumentNullException.ThrowIfNull(patchValidation);

        if (!patchValidation.IsValid)
        {
            return Failure($"Review blocked because the patch is invalid:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", patchValidation.Errors)}");
        }

        try
        {
            var output = await ollamaService.GenerateAsync(
                task.Model,
                BuildReviewPrompt(task, planText, patchText, buildOutput, patchValidation),
                cancellationToken);
            return new LocalCoderResult
            {
                Success = true,
                Output = output,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception exception)
        {
            return Failure($"Local review is unavailable. {exception.Message}");
        }
    }

    private static string BuildReviewPrompt(
        LocalCoderTask task,
        string planText,
        string patchText,
        string buildOutput,
        LocalCoderPatchValidationResult validation)
    {
        return $"""
            You are a local coding reviewer for AiBox.DevPortal.

            Review only. Do not claim files were changed and do not provide commands that modify the system.
            Assess the patch against the task, the plan, the validation result, and the build output.
            Use the same model configured for the task for now.

            Task:
            {task.Instructions}

            Plan:
            {Limit(planText)}

            Unified diff:
            {Limit(patchText)}

            Patch validation result:
            Is valid: {validation.IsValid}
            Touched paths:
            {string.Join(Environment.NewLine, validation.TouchedPaths.Select(path => $"- {path}"))}
            Errors:
            {string.Join(Environment.NewLine, validation.Errors.Select(error => $"- {error}"))}

            Build output:
            {Limit(buildOutput)}

            Answer these questions:
            1. Is this patch likely correct?
            2. What files are changed?
            3. Are there safety concerns?
            4. Did build pass?
            5. What should the user do next?
            6. Should Apply Patch be allowed?

            Return a concise review with those answers and a final recommendation.
            """;
    }

    private static string Limit(string text)
    {
        text ??= string.Empty;
        return text.Length <= MaxSectionCharacters ? text : $"{text[..MaxSectionCharacters]}{Environment.NewLine}[truncated]";
    }

    private static LocalCoderResult Failure(string error) => new()
    {
        Success = false,
        ErrorMessage = error,
        CreatedAt = DateTime.UtcNow
    };
}
