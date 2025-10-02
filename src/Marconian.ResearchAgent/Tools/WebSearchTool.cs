using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Marconian.ResearchAgent.Services.ComputerUse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Tools;

public sealed class WebSearchTool : ITool, IDisposable
{
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ICacheService? _cacheService;
    private readonly string _apiKey;
    private readonly string _searchEngineId;
    private readonly ILogger<WebSearchTool> _logger;
    private readonly WebSearchProvider _provider;
    private readonly ComputerUseSearchService? _computerUseService;

    public WebSearchTool(
        string apiKey,
        string searchEngineId,
        ICacheService? cacheService = null,
        HttpClient? httpClient = null,
        ILogger<WebSearchTool>? logger = null,
        WebSearchProvider provider = WebSearchProvider.GoogleApi,
        ComputerUseSearchService? computerUseService = null)
    {
        _cacheService = cacheService;
        _logger = logger ?? NullLogger<WebSearchTool>.Instance;
        _provider = provider;

        if (_provider == WebSearchProvider.GoogleApi)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new ArgumentException("Google API key must be provided when using the Google search provider.", nameof(apiKey));
            }

            if (string.IsNullOrWhiteSpace(searchEngineId))
            {
                throw new ArgumentException("Google search engine id must be provided when using the Google search provider.", nameof(searchEngineId));
            }

            _apiKey = apiKey;
            _searchEngineId = searchEngineId;

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
        else
        {
            _apiKey = apiKey ?? string.Empty;
            _searchEngineId = searchEngineId ?? string.Empty;
            _computerUseService = computerUseService ?? throw new ArgumentNullException(nameof(computerUseService), "Computer-use search service must be supplied when using the computer-use provider.");
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    public string Name => "WebSearch";

    public string Description => _provider == WebSearchProvider.GoogleApi
        ? "Searches the web using Google Custom Search API and returns curated links with snippets."
        : "Searches the web using Azure computer-use automation (Playwright-controlled Google) and returns curated links with snippets.";

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        string query = context.Parameters.TryGetValue("query", out var providedQuery) && !string.IsNullOrWhiteSpace(providedQuery)
            ? providedQuery
            : context.Instruction;

        if (string.IsNullOrWhiteSpace(query))
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "Search query was not provided."
            };
        }

        string normalizedQuery = query.Trim().ToLowerInvariant();
        string cacheKey = $"tool:websearch:{_provider}:{normalizedQuery}";

        if (_cacheService is not null)
        {
            var cached = await _cacheService.GetAsync<ToolExecutionResult>(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Success)
            {
                _logger.LogDebug("Returning cached web search result for query '{Query}' via provider {Provider}.", query, _provider);
                return cached;
            }
        }

        ToolExecutionResult result = _provider switch
        {
            WebSearchProvider.GoogleApi => await ExecuteGoogleAsync(query, cancellationToken).ConfigureAwait(false),
            WebSearchProvider.ComputerUse => await ExecuteComputerUseAsync(query, cancellationToken).ConfigureAwait(false),
            _ => new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Unsupported search provider: {_provider}"
            }
        };

        if (_cacheService is not null && result.Success)
        {
            await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Cached web search result for query '{Query}' using provider {Provider}.", query, _provider);
        }

        return result;
    }

    private async Task<ToolExecutionResult> ExecuteGoogleAsync(string query, CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "HTTP client not configured for Google search mode.",
                Metadata = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["provider"] = WebSearchProvider.GoogleApi.ToString()
                }
            };
        }

        var requestUri = new Uri($"https://www.googleapis.com/customsearch/v1?key={_apiKey}&cx={_searchEngineId}&q={Uri.EscapeDataString(query)}");

        try
        {
            _logger.LogInformation("Issuing Google Custom Search request for query '{Query}'.", query);
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var citations = new List<SourceCitation>();
            var outputBuilder = new StringBuilder();

            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("items", out JsonElement itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
            {
                int index = 1;
                foreach (JsonElement item in itemsElement.EnumerateArray().Take(5))
                {
                    string title = item.TryGetProperty("title", out JsonElement titleElement) ? titleElement.GetString() ?? "Untitled" : "Untitled";
                    string link = item.TryGetProperty("link", out JsonElement linkElement) ? linkElement.GetString() ?? string.Empty : string.Empty;
                    string snippet = item.TryGetProperty("snippet", out JsonElement snippetElement) ? snippetElement.GetString() ?? string.Empty : string.Empty;

                    citations.Add(new SourceCitation($"gcs:{index}", title, link, snippet));
                    outputBuilder.AppendLine($"{index}. {title}\n{link}\n{snippet}\n");
                    index++;
                }
            }

            var metadata = new Dictionary<string, string>
            {
                ["query"] = query,
                ["provider"] = WebSearchProvider.GoogleApi.ToString()
            };

            if (root.TryGetProperty("searchInformation", out JsonElement searchInfo) && searchInfo.TryGetProperty("totalResults", out JsonElement totalResults))
            {
                metadata["totalResults"] = totalResults.GetString() ?? string.Empty;
            }

            string output = outputBuilder.Length == 0 ? "No results found." : outputBuilder.ToString().Trim();
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = true,
                Output = output,
                Citations = citations,
                Metadata = metadata
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Web search failed for query '{Query}'.", query);
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Web search failed: {ex.Message}",
                Metadata = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["provider"] = WebSearchProvider.GoogleApi.ToString()
                }
            };
        }
    }

    private async Task<ToolExecutionResult> ExecuteComputerUseAsync(string query, CancellationToken cancellationToken)
    {
        if (_computerUseService is null)
        {
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = "Computer-use search service is not configured.",
                Metadata = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["provider"] = WebSearchProvider.ComputerUse.ToString()
                }
            };
        }

        try
        {
            ComputerUseSearchResult searchResult = await _computerUseService.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            if (searchResult.Items.Count == 0)
            {
                return new ToolExecutionResult
                {
                    ToolName = Name,
                    Success = false,
                    ErrorMessage = "No results found via computer-use search.",
                    Metadata = new Dictionary<string, string>
                    {
                        ["query"] = query,
                        ["provider"] = WebSearchProvider.ComputerUse.ToString()
                    }
                };
            }

            var citations = new List<SourceCitation>();
            var outputBuilder = new StringBuilder();
            int index = 1;
            foreach (var item in searchResult.Items.Take(5))
            {
                citations.Add(new SourceCitation($"cus:{index}", item.Title, item.Url, item.Snippet));
                outputBuilder.AppendLine($"{index}. {item.Title}");
                outputBuilder.AppendLine(item.Url);
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                {
                    outputBuilder.AppendLine(item.Snippet);
                }
                outputBuilder.AppendLine();
                index++;
            }

            var metadata = new Dictionary<string, string>
            {
                ["query"] = query,
                ["provider"] = WebSearchProvider.ComputerUse.ToString()
            };

            if (!string.IsNullOrWhiteSpace(searchResult.FinalUrl))
            {
                metadata["finalUrl"] = searchResult.FinalUrl;
            }

            if (searchResult.Transcript.Count > 0)
            {
                metadata["steps"] = string.Join(" | ", searchResult.Transcript.Take(8));
            }

            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = true,
                Output = outputBuilder.ToString().Trim(),
                Citations = citations,
                Metadata = metadata
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PlaywrightException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Computer-use search failed for query '{Query}'.", query);
            return new ToolExecutionResult
            {
                ToolName = Name,
                Success = false,
                ErrorMessage = $"Computer-use search failed: {ex.Message}",
                Metadata = new Dictionary<string, string>
                {
                    ["query"] = query,
                    ["provider"] = WebSearchProvider.ComputerUse.ToString()
                }
            };
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }
}

