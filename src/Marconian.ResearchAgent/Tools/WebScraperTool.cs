using System.Linq;
using System.Net.Http;
using System.Text;
using HtmlAgilityPack;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Tools;

public sealed class WebScraperTool : ITool, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IRedisCacheService? _cacheService;
    private readonly IFileRegistryService? _fileRegistryService;
    private readonly ILogger<WebScraperTool> _logger;
    private const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const int CacheSizeThreshold = 200_000;

    public WebScraperTool(IRedisCacheService? cacheService = null, HttpClient? httpClient = null, ILogger<WebScraperTool>? logger = null, IFileRegistryService? fileRegistryService = null)
    {
        _cacheService = cacheService;
        _logger = logger ?? NullLogger<WebScraperTool>.Instance;
        _fileRegistryService = fileRegistryService;
        if (httpClient is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public string Name => "WebScraper";

    public string Description => "Downloads web page content, optionally rendering dynamic pages with Playwright, and extracts readable text.";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Parameters.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
        {
            url = context.Instruction;
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "No URL provided for scraping."
            };
        }

        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "Invalid URL provided for scraping."
            };
        }

        string normalizedUrl = targetUri.ToString();
        string cacheKey = $"tool:webscrape:{normalizedUrl.ToLowerInvariant()}";

        if (_cacheService is not null)
        {
            var cached = await _cacheService.GetAsync<ToolExecutionResult>(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Success)
            {
                _logger.LogDebug("Returning cached scrape for {Url}.", normalizedUrl);
                return cached;
            }
        }

        bool usePlaywright = context.Parameters.TryGetValue("render", out var renderFlag) && bool.TryParse(renderFlag, out var shouldRender) && shouldRender;

        ScrapePayload payload;
        try
        {
            _logger.LogInformation("Scraping {Url} (render={Render}).", normalizedUrl, usePlaywright);
            payload = usePlaywright
                ? ScrapePayload.ForHtml(await LoadWithPlaywrightAsync(normalizedUrl).ConfigureAwait(false), true)
                : await LoadWithHttpClientAsync(targetUri, cancellationToken).ConfigureAwait(false);

            if (!usePlaywright && payload.IsHtml && IsUnsupportedBrowserPage(payload.HtmlContent))
            {
                _logger.LogInformation("Detected unsupported browser response for {Url}. Retrying with Playwright.", normalizedUrl);
                payload.Dispose();
                payload = ScrapePayload.ForHtml(await LoadWithPlaywrightAsync(normalizedUrl).ConfigureAwait(false), true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlaywrightException)
        {
            _logger.LogWarning(ex, "Failed to fetch {Url}.", normalizedUrl);
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Failed to fetch page: {ex.Message}",
                Metadata = new Dictionary<string, string>
                {
                    ["url"] = normalizedUrl,
                    ["render"] = usePlaywright.ToString()
                }
            };
        }

        if (payload.IsBinary && _fileRegistryService is not null)
        {
            return await PersistBinaryAsync(context, normalizedUrl, targetUri, payload, cancellationToken).ConfigureAwait(false);
        }

        string html = payload.HtmlContent ?? string.Empty;
        string extractedText = ExtractReadableText(html);
        payload.Dispose();

        if (string.IsNullOrWhiteSpace(extractedText))
        {
            extractedText = "No readable text was extracted from the page.";
        }

        string title = ExtractTitle(html) ?? normalizedUrl;
        string snippet = extractedText.Length > 400 ? extractedText[..400] + "…" : extractedText;

        var result = new ToolExecutionResult
        {
            ToolName = Name,
            Success = true,
            Output = extractedText,
            Citations =
            {
                new SourceCitation($"scrape:{normalizedUrl.GetHashCode():X}", title, normalizedUrl, snippet)
            },
            Metadata = new Dictionary<string, string>
            {
                ["url"] = normalizedUrl,
                ["render"] = usePlaywright.ToString()
            }
        };

        if (_cacheService is not null && extractedText.Length <= CacheSizeThreshold)
        {
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Cached scrape result for {Url}.", normalizedUrl);
        }
        else if (_cacheService is not null)
        {
            _logger.LogDebug("Skipping cache for {Url} due to payload size {Length}.", normalizedUrl, extractedText.Length);
        }

        return result;
    }

    private async Task<ScrapePayload> LoadWithHttpClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _logger.LogTrace("Fetched content via HttpClient from {Url}.", uri);

        string? contentType = response.Content.Headers.ContentType?.MediaType;
        bool isBinary = contentType is not null && !contentType.StartsWith("text", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

        if (isBinary)
        {
            var memoryStream = new MemoryStream();
            await response.Content.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            memoryStream.Position = 0;
            return ScrapePayload.ForBinary(memoryStream, contentType);
        }

        string html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ScrapePayload.ForHtml(html, false, contentType);
    }

    private static async Task<string> LoadWithPlaywrightAsync(string url)
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });

            try
            {
                var page = await browser.NewPageAsync();
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle,
                    Timeout = 30000
                });

                string content = await page.ContentAsync();
                await page.CloseAsync();
                return content;
            }
            finally
            {
                await browser.CloseAsync();
            }
        }
        finally
        {
            playwright.Dispose();
        }
    }

    private async Task<ToolExecutionResult> PersistBinaryAsync(
        ToolExecutionContext context,
        string normalizedUrl,
        Uri targetUri,
        ScrapePayload payload,
        CancellationToken cancellationToken)
    {
        if (_fileRegistryService is null || payload.BinaryContent is null)
        {
            payload.Dispose();
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "Binary content detected, but no file registry is configured to persist it.",
                Metadata = new Dictionary<string, string>
                {
                    ["url"] = normalizedUrl,
                    ["contentType"] = payload.ContentType ?? string.Empty
                }
            };
        }

        await using var stream = payload.BinaryContent;
        string fileName = Path.GetFileName(targetUri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "download";
        }

        var entry = await _fileRegistryService.SaveFileAsync(
            context.ResearchSessionId,
            fileName,
            stream,
            payload.ContentType,
            normalizedUrl,
            cancellationToken).ConfigureAwait(false);

        payload.Dispose();

        return new ToolExecutionResult
        {
            ToolName = Name,
            Success = true,
            Output = $"Binary content stored as '{entry.FileName}' (file ID: {entry.FileId}).",
            Metadata = new Dictionary<string, string>
            {
                ["url"] = normalizedUrl,
                ["contentType"] = payload.ContentType ?? string.Empty,
                ["fileId"] = entry.FileId,
                ["fileName"] = entry.FileName,
                ["filePath"] = entry.RelativePath
            },
            Citations =
            {
                new SourceCitation(entry.FileId, entry.FileName, normalizedUrl, "Binary content captured in registry.")
            }
        };
    }


    private static string ExtractReadableText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var sb = new StringBuilder();
        foreach (var node in document.DocumentNode.SelectNodes("//p") ?? Enumerable.Empty<HtmlNode>())
        {
            string text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            sb.AppendLine(text);
        }

        return sb.ToString().Trim();
    }

    private static string? ExtractTitle(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectSingleNode("//title")?.InnerText?.Trim();
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        await Task.CompletedTask;
    }

    private static bool IsUnsupportedBrowserPage(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return false;
        }

        const string marker = "This browser is no longer supported";
        return html.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private sealed class ScrapePayload : IDisposable
    {
        private ScrapePayload(string? html, Stream? binaryStream, bool renderedWithPlaywright, string? contentType)
        {
            HtmlContent = html;
            BinaryContent = binaryStream;
            RenderedWithPlaywright = renderedWithPlaywright;
            ContentType = contentType;
        }

        public string? HtmlContent { get; }
        public Stream? BinaryContent { get; private set; }
        public bool RenderedWithPlaywright { get; }
        public string? ContentType { get; }

        public bool IsBinary => BinaryContent is not null;
        public bool IsHtml => HtmlContent is not null;

        public static ScrapePayload ForHtml(string html, bool renderedWithPlaywright, string? contentType = null)
            => new(html, null, renderedWithPlaywright, contentType);

        public static ScrapePayload ForBinary(Stream stream, string? contentType)
            => new(null, stream, false, contentType);

        public void Dispose()
        {
            BinaryContent?.Dispose();
            BinaryContent = null;
        }
    }
}
