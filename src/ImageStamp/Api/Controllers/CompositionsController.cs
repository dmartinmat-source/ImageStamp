using System.Text.Json;

using ImageStamp.Api.Contracts;
using ImageStamp.Core.Imaging;
using ImageStamp.Core.Models;
using ImageStamp.Core.Options;
using ImageStamp.Core.Validation;
using ImageStamp.Infrastructure.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using SixLabors.ImageSharp;

namespace ImageStamp.Api.Controllers;

[ApiController]
[Route("api/compositions")]
public sealed class CompositionsController(
    ImageCompositionService compositionService,
    CompositionRepository repository,
    IOptions<ImageProcessingOptions> options,
    ILogger<CompositionsController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions LayerJsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ImageProcessingOptions _options = options.Value;

    /// <summary>
    /// Composes a base PNG with the submitted layers and returns the resulting PNG.
    /// </summary>
    /// <remarks>
    /// Expects a <c>multipart/form-data</c> body with:
    /// <list type="bullet">
    /// <item><description><c>baseImage</c>: the background PNG file.</description></item>
    /// <item><description><c>layers</c>: a JSON array describing the layers.</description></item>
    /// <item><description>one additional file per image layer, named after its <c>imageKey</c>.</description></item>
    /// </list>
    /// </remarks>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [Produces("image/png", "application/problem+json")]
    public async Task<IActionResult> Create(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return Problem("The request must be sent as multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);
        }

        IFormCollection form = await Request.ReadFormAsync(cancellationToken);

        IFormFile? baseImageFile = form.Files["baseImage"];

        if (baseImageFile is null)
        {
            return Problem("A 'baseImage' file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? uploadError = ValidateUpload(baseImageFile);

        if (uploadError is not null)
        {
            return Problem(uploadError, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseLayerRequests(form, out LayerRequest[] layerRequests, out string? layerRequestError))
        {
            return Problem(layerRequestError, statusCode: StatusCodes.Status400BadRequest);
        }

        RequestBuildResult requestBuildResult = await BuildCompositionRequestAsync(
            form,
            baseImageFile,
            layerRequests,
            cancellationToken);

        if (requestBuildResult.Error is not null)
        {
            return Problem(requestBuildResult.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        CompositionRequest compositionRequest = requestBuildResult.Request!;

        if (!CompositionValidation.TryValidate(compositionRequest, _options, out string validationError))
        {
            return Problem(validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        CompositionExecutionResult executionResult = await ComposeAndStoreAsync(compositionRequest, cancellationToken);

        if (executionResult.Error is not null)
        {
            return Problem(executionResult.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        Response.Headers["X-Composition-Id"] = executionResult.CompositionId!.Value.ToString();

        return File(executionResult.Result!.Png, "image/png");
    }

    /// <summary>
    /// Composes several base PNGs with shared layers and returns the results as a ZIP archive.
    /// </summary>
    [HttpPost("batch")]
    [Consumes("multipart/form-data")]
    [Produces("application/zip", "application/problem+json")]
    public async Task<IActionResult> CreateBatch(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return Problem("The request must be sent as multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);
        }

        IFormCollection form = await Request.ReadFormAsync(cancellationToken);
        IReadOnlyList<IFormFile> baseImageFiles = form.Files.GetFiles("baseImages");

        if (baseImageFiles.Count == 0)
        {
            return Problem("At least one 'baseImages' file is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        foreach (IFormFile baseImageFile in baseImageFiles)
        {
            string? uploadError = ValidateUpload(baseImageFile);

            if (uploadError is not null)
            {
                return Problem(uploadError, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (!TryParseLayerRequests(form, out LayerRequest[] layerRequests, out string? layerRequestError))
        {
            return Problem(layerRequestError, statusCode: StatusCodes.Status400BadRequest);
        }

        List<PngArchiveEntry> archiveEntries = new List<PngArchiveEntry>(baseImageFiles.Count);

        foreach (IFormFile baseImageFile in baseImageFiles)
        {
            RequestBuildResult requestBuildResult = await BuildCompositionRequestAsync(
                form,
                baseImageFile,
                layerRequests,
                cancellationToken);

            if (requestBuildResult.Error is not null)
            {
                return Problem(requestBuildResult.Error, statusCode: StatusCodes.Status400BadRequest);
            }

            CompositionRequest compositionRequest = requestBuildResult.Request!;

            if (!CompositionValidation.TryValidate(compositionRequest, _options, out string validationError))
            {
                return Problem(validationError, statusCode: StatusCodes.Status400BadRequest);
            }

            CompositionExecutionResult executionResult = await ComposeAndStoreAsync(compositionRequest, cancellationToken);

            if (executionResult.Error is not null)
            {
                return Problem(executionResult.Error, statusCode: StatusCodes.Status400BadRequest);
            }

            archiveEntries.Add(new PngArchiveEntry(baseImageFile.FileName, executionResult.Result!.Png));
        }

        return File(PngArchiveBuilder.Create(archiveEntries), "application/zip", "compositions.zip");
    }

    /// <summary>
    /// Lists the stored compositions, most recent first.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CompositionResponse>>> GetAll(CancellationToken cancellationToken)
    {
        List<Composition> compositions = await repository.GetCompositionsAsync(cancellationToken);

        foreach (Composition composition in compositions)
        {
            composition.Layers = await repository.GetLayersAsync(composition.Id, cancellationToken);
        }

        List<CompositionResponse> response = new List<CompositionResponse>(compositions.Count);

        foreach (Composition composition in compositions)
        {
            response.Add(CompositionResponse.From(composition));
        }

        return Ok(response);
    }

    /// <summary>
    /// Returns a single composition together with its layers.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CompositionResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        Composition? composition = await repository.GetCompositionAsync(id, cancellationToken);

        if (composition is null)
        {
            return NotFound();
        }

        composition.Layers = await repository.GetLayersAsync(composition.Id, cancellationToken);

        return Ok(CompositionResponse.From(composition));
    }

    private string? ValidateUpload(IFormFile file)
    {
        if (file.Length == 0)
        {
            return "The uploaded file '" + file.FileName + "' is empty.";
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            return "The uploaded file '" + file.FileName + "' is larger than the "
                + _options.MaxUploadBytes + " byte limit.";
        }

        if (!_options.AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return "The content type '" + file.ContentType + "' is not accepted. Expected one of: "
                + string.Join(", ", _options.AllowedContentTypes) + ".";
        }

        return null;
    }

    private static bool TryParseLayerRequests(IFormCollection form, out LayerRequest[] layerRequests, out string? error)
    {
        try
        {
            string json = form["layers"].ToString();
            layerRequests = string.IsNullOrWhiteSpace(json)
                ? Array.Empty<LayerRequest>()
                : JsonSerializer.Deserialize<LayerRequest[]>(json, LayerJsonOptions) ?? Array.Empty<LayerRequest>();
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            layerRequests = Array.Empty<LayerRequest>();
            error = "The 'layers' field is not valid JSON: " + exception.Message;
            return false;
        }
    }

    private async Task<RequestBuildResult> BuildCompositionRequestAsync(
        IFormCollection form,
        IFormFile baseImageFile,
        LayerRequest[] layerRequests,
        CancellationToken cancellationToken)
    {
        CompositionRequest compositionRequest = new CompositionRequest();
        compositionRequest.BaseImageFileName = baseImageFile.FileName;

        byte[] baseImageContent = await StreamHelpers.ReadFullyAsync(baseImageFile.OpenReadStream(), cancellationToken);
        compositionRequest.BaseImage = new MemoryStream(baseImageContent, writable: false);

        for (int i = 0; i < layerRequests.Length; i++)
        {
            if (!TryCreateLayer(layerRequests[i], i, form, out Layer? layer, out string? error))
            {
                return new RequestBuildResult(null, error);
            }

            compositionRequest.Layers.Add(layer!);
        }

        return new RequestBuildResult(compositionRequest, null);
    }

    private bool TryCreateLayer(
        LayerRequest layerRequest,
        int index,
        IFormCollection form,
        out Layer? layer,
        out string? error)
    {
        layer = new Layer();
        layer.Type = (layerRequest.Type ?? string.Empty).Trim().ToLowerInvariant();
        layer.X = layerRequest.X;
        layer.Y = layerRequest.Y;
        layer.ZIndex = layerRequest.ZIndex;
        layer.Opacity = layerRequest.Opacity;

        if (layer.Type == LayerTypes.Image)
        {
            IFormFile? layerFile = string.IsNullOrWhiteSpace(layerRequest.ImageKey)
                ? null
                : form.Files[layerRequest.ImageKey];

            if (layerFile is null)
            {
                error = "Layer " + index + ": no uploaded file matches the image key '" + layerRequest.ImageKey + "'.";
                return false;
            }

            string? uploadError = ValidateUpload(layerFile);

            if (uploadError is not null)
            {
                error = "Layer " + index + ": " + uploadError;
                return false;
            }

            layer.FileName = layerFile.FileName;
            layer.Image = layerFile.OpenReadStream();
        }
        else if (layer.Type == LayerTypes.Solid)
        {
            layer.Width = layerRequest.Width;
            layer.Height = layerRequest.Height;
            layer.Color = layerRequest.Color;
        }
        else if (layer.Type == LayerTypes.Blur)
        {
            layer.Width = layerRequest.Width;
            layer.Height = layerRequest.Height;
            layer.Sigma = layerRequest.Sigma;
        }
        else
        {
            error = "Layer " + index + ": unsupported layer type '" + layerRequest.Type + "'.";
            return false;
        }

        error = null;
        return true;
    }

    private sealed record RequestBuildResult(CompositionRequest? Request, string? Error);

    private sealed record CompositionExecutionResult(
        Guid? CompositionId,
        CompositionResult? Result,
        string? Error);

    private async Task<CompositionExecutionResult> ComposeAndStoreAsync(
        CompositionRequest request,
        CancellationToken cancellationToken)
    {
        Composition composition = CreatePendingComposition(request);
        await repository.CreateCompositionAsync(composition, cancellationToken);

        CompositionResult result;

        try
        {
            result = await compositionService.ComposeAsync(request, cancellationToken);
        }
        catch (UnknownImageFormatException exception)
        {
            await repository.MarkFailedAsync(composition.Id, exception.Message, cancellationToken);
            return new CompositionExecutionResult(null, null, "One of the uploaded files is not a supported image.");
        }
        catch (ImageFormatException exception)
        {
            await repository.MarkFailedAsync(composition.Id, exception.Message, cancellationToken);
            return new CompositionExecutionResult(null, null, "One of the uploaded images could not be decoded.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Composition {CompositionId} failed.", composition.Id);
            await repository.MarkFailedAsync(composition.Id, exception.Message, cancellationToken);
            throw;
        }

        await StoreLayersAsync(composition.Id, request.Layers, cancellationToken);
        await repository.MarkCompletedAsync(
            composition.Id,
            result.OutputSizeBytes,
            (int)result.ProcessingTimeMs,
            cancellationToken);

        return new CompositionExecutionResult(composition.Id, result, null);
    }

    private static Composition CreatePendingComposition(CompositionRequest request)
    {
        return new Composition
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = CompositionStatus.Pending,
            BaseImageFileName = request.BaseImageFileName,
            LayerCount = request.Layers.Count,
        };
    }

    private async Task StoreLayersAsync(Guid compositionId, IEnumerable<Layer> layers, CancellationToken cancellationToken)
    {
        foreach (Layer layer in layers)
        {
            await repository.AddLayerAsync(CreateStoredLayer(compositionId, layer), cancellationToken);
        }
    }

    private static CompositionLayer CreateStoredLayer(Guid compositionId, Layer layer)
    {
        return new CompositionLayer
        {
            Id = Guid.NewGuid(),
            CompositionId = compositionId,
            LayerType = layer.Type,
            X = layer.X,
            Y = layer.Y,
            Opacity = layer.Opacity,
            ZIndex = layer.ZIndex,
            FileName = layer.FileName,
            Width = layer.Width,
            Height = layer.Height,
            Color = layer.Color,
            Sigma = layer.Sigma,
        };
    }
}
