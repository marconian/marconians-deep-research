using System.Linq;
using System.Net.Http;
using System.Text;
using HtmlAgilityPack;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Tools;

public sealed class WebScraperTool : ITool, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IRedisCacheService? _cacheService;

    public WebScraperTool(IRedisCacheService? cacheService = null, HttpClient? httpClient = null)
    {
        _cacheService = cacheService;
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
                return cached;
            }
        }

        bool usePlaywright = context.Parameters.TryGetValue("render", out var renderFlag) && bool.TryParse(renderFlag, out var shouldRender) && shouldRender;

        string html;
        try
        {
            html = usePlaywright
                ? await LoadWithPlaywrightAsync(normalizedUrl).ConfigureAwait(false)
                : await LoadWithHttpClientAsync(targetUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlaywrightException)
        {
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

        string extractedText = ExtractReadableText(html);
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

        if (_cacheService is not null)
        {
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<string> LoadWithHttpClientAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; MarconianBot/1.0)");
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
}
