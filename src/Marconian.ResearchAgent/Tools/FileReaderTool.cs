using System.Linq;
using System.Net.Http;
using System.Text;
using Marconian.ResearchAgent.Models.Files;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Files;

namespace Marconian.ResearchAgent.Tools;

public sealed class FileReaderTool : ITool, IDisposable
{
    private readonly IFileRegistryService _fileRegistry;
    private readonly IDocumentIntelligenceService _documentService;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public FileReaderTool(IFileRegistryService fileRegistry, IDocumentIntelligenceService documentService, HttpClient? httpClient = null)
    {
        _fileRegistry = fileRegistry ?? throw new ArgumentNullException(nameof(fileRegistry));
        _documentService = documentService ?? throw new ArgumentNullException(nameof(documentService));

        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public string Name => "FileReader";

    public string Description => "Reads documents from the local registry or downloads new files, extracting text using Document Intelligence when needed.";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(context.ResearchSessionId))
        {
            return Failure("Research session identifier is required to read files.");
        }

        string? fileId = context.Parameters.TryGetValue("fileId", out var providedFileId) ? providedFileId : null;
        string? url = context.Parameters.TryGetValue("url", out var providedUrl) ? providedUrl : null;
        string? path = context.Parameters.TryGetValue("path", out var providedPath) ? providedPath : null;

        Stream? dataStream = null;
        string contentType = "application/octet-stream";
        string fileName;
        string? sourceUrl = null;
        string? resolvedFileId = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                var entry = await _fileRegistry.GetEntryAsync(context.ResearchSessionId, fileId, cancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    return Failure($"File with id '{fileId}' was not found in the registry.");
                }

                dataStream = await _fileRegistry.OpenFileAsync(context.ResearchSessionId, fileId, cancellationToken).ConfigureAwait(false);
                if (dataStream is null)
                {
                    return Failure("File entry existed but the underlying file could not be opened.");
                }

                fileName = entry.FileName;
                sourceUrl = entry.SourceUrl;
                contentType = entry.ContentType ?? GuessContentTypeFromName(fileName);
                resolvedFileId = entry.FileId;
            }
            else if (!string.IsNullOrWhiteSpace(url))
            {
                var downloadResult = await DownloadAndRegisterAsync(url, context.ResearchSessionId, cancellationToken).ConfigureAwait(false);
                dataStream = downloadResult.Stream;
                fileName = downloadResult.Entry.FileName;
                contentType = downloadResult.Entry.ContentType ?? GuessContentTypeFromName(fileName);
                sourceUrl = downloadResult.Entry.SourceUrl;
                resolvedFileId = downloadResult.Entry.FileId;
            }
            else if (!string.IsNullOrWhiteSpace(path))
            {
                string absolutePath = Path.GetFullPath(path);
                if (!File.Exists(absolutePath))
                {
                    return Failure($"Local file '{path}' does not exist.");
                }

                dataStream = File.OpenRead(absolutePath);
                fileName = Path.GetFileName(absolutePath);
                contentType = GuessContentTypeFromName(fileName);
            }
            else
            {
                return Failure("Provide either a fileId, url, or path parameter.");
            }

            using (dataStream)
            {
                string extractedText = await ExtractTextAsync(dataStream, contentType, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(extractedText))
                {
                    extractedText = "Document contained no readable text.";
                }

                string snippet = extractedText.Length > 400 ? extractedText[..400] + "…" : extractedText;

                var result = new ToolExecutionResult
                {
                    ToolName = Name,
                    Success = true,
                    Output = extractedText,
                    Citations =
                    {
                        new SourceCitation(resolvedFileId is null ? $"local:{fileName}" : $"file:{resolvedFileId}", fileName, sourceUrl, snippet)
                    },
                    Metadata = new Dictionary<string, string>
                    {
                        ["fileId"] = resolvedFileId ?? string.Empty,
                        ["fileName"] = fileName,
                        ["contentType"] = contentType
                    }
                };

                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return Failure($"File processing failed: {ex.Message}");
        }
    }

    private async Task<(FileRegistryEntry Entry, Stream Stream)> DownloadAndRegisterAsync(string url, string researchSessionId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string fileName = Path.GetFileName(response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? new Uri(url).Segments.LastOrDefault() ?? "downloaded-file");
        string contentType = response.Content.Headers.ContentType?.MediaType ?? GuessContentTypeFromName(fileName);

        await using Stream remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        await remoteStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        var entry = await _fileRegistry.SaveFileAsync(researchSessionId, fileName, buffer, contentType, url, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        return (entry, buffer);
    }

    private async Task<string> ExtractTextAsync(Stream stream, string contentType, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        if (IsPdf(contentType))
        {
            var analysis = await _documentService.AnalyzeDocumentAsync(stream, contentType, cancellationToken).ConfigureAwait(false);
            return analysis.Text;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return text;
    }

    private static bool IsPdf(string contentType)
        => contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    private static string GuessContentTypeFromName(string fileName)
    {
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "application/pdf";
        }

        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return "text/plain";
        }

        if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return "text/markdown";
        }

        return "application/octet-stream";
    }

    private static ToolExecutionResult Failure(string message) => new()
    {
        ToolName = "FileReader",
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
