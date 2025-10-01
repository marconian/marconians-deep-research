using System.Drawing;
using System.Linq;
using System.Net.Http;
using Marconian.ResearchAgent.Models.Files;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Files;
using Marconian.ResearchAgent.Services.OpenAI;
using Marconian.ResearchAgent.Services.OpenAI.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Tools;

public sealed class ImageReaderTool : ITool, IDisposable
{
    private readonly IFileRegistryService _fileRegistry;
    private readonly IAzureOpenAiService _openAiService;
    private readonly string _visionDeploymentName;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<ImageReaderTool> _logger;

    public ImageReaderTool(
        IFileRegistryService fileRegistry,
        IAzureOpenAiService openAiService,
        string visionDeploymentName,
        HttpClient? httpClient = null,
        ILogger<ImageReaderTool>? logger = null)
    {
        _fileRegistry = fileRegistry ?? throw new ArgumentNullException(nameof(fileRegistry));
        _openAiService = openAiService ?? throw new ArgumentNullException(nameof(openAiService));
        _visionDeploymentName = string.IsNullOrWhiteSpace(visionDeploymentName)
            ? throw new ArgumentException("Vision deployment name must be provided.", nameof(visionDeploymentName))
            : visionDeploymentName;
        _logger = logger ?? NullLogger<ImageReaderTool>.Instance;

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public string Name => "ImageReader";

    public string Description => "Analyzes images from the registry or remote URLs and describes key visual elements.";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.ResearchSessionId))
        {
            return Failure("Research session id is required for image analysis.");
        }

        string? fileId = context.Parameters.TryGetValue("fileId", out var providedFileId) ? providedFileId : null;
        string? url = context.Parameters.TryGetValue("url", out var providedUrl) ? providedUrl : null;

        Stream? imageStream = null;
        string fileName;
        string? sourceUrl = null;
        string contentType = "image/png";
        string? resolvedFileId = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                var entry = await _fileRegistry.GetEntryAsync(context.ResearchSessionId, fileId, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    _logger.LogWarning("Image {FileId} not found in registry for session {SessionId}.", fileId, context.ResearchSessionId);
                    return Failure($"Image with id '{fileId}' was not found.");
                }

                imageStream = await _fileRegistry.OpenFileAsync(context.ResearchSessionId, fileId, cancellationToken).ConfigureAwait(false);
                if (imageStream is null)
                {
                    _logger.LogWarning("Image entry {FileId} existed but file stream unavailable.", fileId);
                    return Failure("Image entry existed but the file could not be opened.");
                }

                fileName = entry.FileName;
                sourceUrl = entry.SourceUrl;
                contentType = entry.ContentType ?? GuessImageContentType(fileName);
                resolvedFileId = entry.FileId;
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                _logger.LogInformation("Downloading image from {Url} for session {SessionId}.", url, context.ResearchSessionId);
                var downloadResult = await DownloadAndRegisterAsync(url, context.ResearchSessionId, cancellationToken).ConfigureAwait(false);
                imageStream = downloadResult.Stream;
                fileName = downloadResult.Entry.FileName;
                sourceUrl = downloadResult.Entry.SourceUrl;
                contentType = downloadResult.Entry.ContentType ?? GuessImageContentType(fileName);
                resolvedFileId = downloadResult.Entry.FileId;
            }
            else
            {
                _logger.LogWarning("ImageReader invoked without fileId or url.");
                return Failure("Provide either a fileId or url parameter for image analysis.");
            }

            using (imageStream)
            {
                var metadata = ExtractMetadata(imageStream);
                imageStream.Position = 0;
                string analysis = await DescribeImageAsync(imageStream, metadata, contentType, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Image analysis produced {Length} characters for session {SessionId}.", analysis.Length, context.ResearchSessionId);

                var result = new ToolExecutionResult
                {
                    ToolName = Name,
                    Success = true,
                    Output = analysis,
                    Citations =
                    {
                        new SourceCitation(resolvedFileId is null ? $"image:{fileName}" : $"image:{resolvedFileId}", fileName, sourceUrl, metadata)
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["fileId"] = resolvedFileId ?? string.Empty,
                        ["fileName"] = fileName,
                        ["contentType"] = contentType,
                        ["dimensions"] = metadata
                    }
                };

                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            _logger.LogError(ex, "Image analysis failed for session {SessionId}.", context.ResearchSessionId);
            return Failure($"Image analysis failed: {ex.Message}");
        }
    }

    private async Task<(FileRegistryEntry Entry, Stream Stream)> DownloadAndRegisterAsync(string url, string researchSessionId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string fileName = Path.GetFileName(response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? new Uri(url).Segments.LastOrDefault() ?? "image");
        string contentType = response.Content.Headers.ContentType?.MediaType ?? GuessImageContentType(fileName);

        await using Stream remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        await remoteStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        var entry = await _fileRegistry.SaveFileAsync(researchSessionId, fileName, buffer, contentType, url, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Downloaded image {FileName} stored with id {FileId}.", fileName, entry.FileId);
        buffer.Position = 0;
        return (entry, buffer);
    }

    private static string ExtractMetadata(Stream stream)
    {
#pragma warning disable CA1416
        stream.Position = 0;
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        string format = image.RawFormat?.ToString() ?? "unknown";
        return $"{image.Width}x{image.Height} ({format})";
#pragma warning restore CA1416
    }

    private async Task<string> DescribeImageAsync(Stream stream, string metadata, string contentType, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        memoryStream.Position = 0;
        string base64 = Convert.ToBase64String(memoryStream.ToArray());

        var request = new OpenAiChatRequest(
            SystemPrompt: "You are a computer vision specialist who describes images and highlights important findings concisely.",
            Messages: new[]
            {
                new OpenAiChatMessage("user", $"The image has metadata {metadata} and content type {contentType}. Analyze the scene and describe key details. The image data is provided as base64 below.\nBASE64:{base64}")
            },
            DeploymentName: _visionDeploymentName,
            MaxOutputTokens: 300);

        try
        {
            return await _openAiService.GenerateTextAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision model unavailable; returning metadata summary.");
            return $"Vision model unavailable. Metadata: {metadata}. Error: {ex.Message}";
        }
    }

    private static string GuessImageContentType(string fileName)
    {
        if (fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "image/png";
        }

        if (fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return "image/jpeg";
        }

        if (fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            return "image/gif";
        }

        return "application/octet-stream";
    }

    private static ToolExecutionResult Failure(string message) => new()
    {
        ToolName = "ImageReader",
        Success = false,
        ErrorMessage = message
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

