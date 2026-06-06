using AiBox.DevPortal.Models;
using AiBox.DevPortal.Services;

namespace AiBox.DevPortal.Api;

public static class ImageToolEndpoints
{
    private const long MaxUploadBytes = 100 * 1024 * 1024;

    public static IEndpointRouteBuilder MapImageToolEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/image-tools")
            .WithTags("Image Tools");

        group.MapGet("/", () => Results.Ok(new
            {
                name = "AiBox Image Tools",
                endpoints = new[]
                {
                    "GET /api/image-tools/health",
                    "GET /api/image-tools/models",
                    "POST /api/image-tools/text-to-image",
                    "POST /api/image-tools/image-to-image",
                    "POST /api/image-tools/face-detailer",
                    "POST /api/image-tools/upscale",
                    "POST /api/image-tools/lora"
                }
            }))
            .WithName("GetImageTools")
            .WithSummary("Lists available image-tool endpoints.");

        group.MapGet("/health", async (IComfyUiService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetHealthAsync(cancellationToken)))
            .WithName("GetImageToolsHealth")
            .WithSummary("Checks whether the ComfyUI image backend is available.");

        group.MapGet("/models", async (IComfyUiService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.GetModelsAsync(cancellationToken)))
            .WithName("GetImageToolModels")
            .WithSummary("Lists installed image models, LoRAs, and upscalers.");

        group.MapPost("/text-to-image", (TextToImageToolRequest request, IImageToolService service, CancellationToken cancellationToken) =>
                ExecuteAsync(() => service.TextToImageAsync(request, cancellationToken)))
            .WithName("TextToImage")
            .WithSummary("Generates an SDXL or Flux image from a text prompt.")
            .Produces<ComfyUiGenerationResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/image-to-image", async (HttpRequest httpRequest, IImageToolService service, CancellationToken cancellationToken) =>
                await ExecuteFormAsync(httpRequest, (form, image, stream) =>
                {
                    var request = new ImageToImageToolRequest
                    {
                        Model = FormValue(form, "model", "sdxl"),
                        Prompt = FormValue(form, "prompt"),
                        NegativePrompt = FormValue(form, "negativePrompt"),
                        Steps = FormInt(form, "steps", 20),
                        Denoise = FormDouble(form, "denoise", 0.55),
                        Seed = FormLong(form, "seed", -1)
                    };
                    return service.ImageToImageAsync(request, image.FileName, stream, cancellationToken);
                }, cancellationToken))
            .DisableAntiforgery()
            .WithName("ImageToImage")
            .WithSummary("Transforms an uploaded image with SDXL or Flux.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ComfyUiGenerationResult>();

        group.MapPost("/face-detailer", async (HttpRequest httpRequest, IImageToolService service, CancellationToken cancellationToken) =>
                await ExecuteFormAsync(httpRequest, (form, image, stream) =>
                {
                    var request = new FaceDetailerToolRequest
                    {
                        Prompt = FormValue(form, "prompt", new FaceDetailerToolRequest().Prompt),
                        NegativePrompt = FormValue(form, "negativePrompt", new FaceDetailerToolRequest().NegativePrompt),
                        Denoise = FormDouble(form, "denoise", 0.35),
                        Seed = FormLong(form, "seed", -1)
                    };
                    return service.FaceDetailerAsync(request, image.FileName, stream, cancellationToken);
                }, cancellationToken))
            .DisableAntiforgery()
            .WithName("FaceDetailer")
            .WithSummary("Improves faces in an uploaded image.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ComfyUiGenerationResult>();

        group.MapPost("/upscale", async (HttpRequest httpRequest, IImageToolService service, CancellationToken cancellationToken) =>
                await ExecuteFormAsync(httpRequest, (form, image, stream) =>
                {
                    var request = new UpscaleToolRequest
                    {
                        Upscaler = FormValue(form, "upscaler"),
                        Scale = FormInt(form, "scale", 2)
                    };
                    return service.UpscaleAsync(request, image.FileName, stream, cancellationToken);
                }, cancellationToken))
            .DisableAntiforgery()
            .WithName("UpscaleImage")
            .WithSummary("Upscales an uploaded image.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<ComfyUiGenerationResult>();

        group.MapPost("/lora", (LoraToolRequest request, IImageToolService service, CancellationToken cancellationToken) =>
                ExecuteAsync(() => service.LoraAsync(request, cancellationToken)))
            .WithName("GenerateLoraImage")
            .WithSummary("Generates an SDXL or Flux image using an installed LoRA.")
            .Produces<ComfyUiGenerationResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> ExecuteFormAsync(
        HttpRequest request,
        Func<IFormCollection, IFormFile, Stream, Task<ComfyUiGenerationResult>> action,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Problem(StatusCodes.Status400BadRequest, "Content-Type must be multipart/form-data.");
        }

        try
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var image = form.Files.GetFile("image");

            if (image is null || image.Length == 0)
            {
                return Problem(StatusCodes.Status400BadRequest, "A non-empty image file is required in the 'image' field.");
            }

            if (image.Length > MaxUploadBytes)
            {
                return Problem(StatusCodes.Status400BadRequest, "Image must not exceed 100 MB.");
            }

            if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return Problem(StatusCodes.Status400BadRequest, "The uploaded file must have an image content type.");
            }

            await using var stream = image.OpenReadStream();
            return Results.Ok(await action(form, image, stream));
        }
        catch (Exception exception)
        {
            return ExceptionResult(exception);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<ComfyUiGenerationResult>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (Exception exception)
        {
            return ExceptionResult(exception);
        }
    }

    private static IResult ExceptionResult(Exception exception)
    {
        return exception switch
        {
            ArgumentException => Problem(StatusCodes.Status400BadRequest, exception.Message),
            FileNotFoundException => Problem(StatusCodes.Status503ServiceUnavailable, exception.Message),
            InvalidOperationException => Problem(StatusCodes.Status503ServiceUnavailable, exception.Message),
            HttpRequestException => Problem(StatusCodes.Status503ServiceUnavailable, exception.Message),
            TimeoutException => Problem(StatusCodes.Status504GatewayTimeout, exception.Message),
            _ => Problem(StatusCodes.Status500InternalServerError, "Image generation failed.")
        };
    }

    private static IResult Problem(int statusCode, string detail)
    {
        return Results.Problem(statusCode: statusCode, detail: detail);
    }

    private static string FormValue(IFormCollection form, string name, string fallback = "")
    {
        var value = form[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static int FormInt(IFormCollection form, string name, int fallback)
    {
        var rawValue = form[name].ToString();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        return int.TryParse(rawValue, out var value)
            ? value
            : throw new ArgumentException($"{name} must be an integer.", name);
    }

    private static long FormLong(IFormCollection form, string name, long fallback)
    {
        var rawValue = form[name].ToString();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        return long.TryParse(rawValue, out var value)
            ? value
            : throw new ArgumentException($"{name} must be an integer.", name);
    }

    private static double FormDouble(IFormCollection form, string name, double fallback)
    {
        var rawValue = form[name].ToString();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return fallback;
        }

        return double.TryParse(rawValue, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"{name} must be a number.", name);
    }
}
