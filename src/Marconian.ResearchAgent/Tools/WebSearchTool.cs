using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Marconian.ResearchAgent.Models.Reporting;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.Caching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Tools;

public sealed class WebSearchTool : ITool, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ICacheService? _cacheService;
    private readonly string _apiKey;
    private readonly string _searchEngineId;
    private readonly ILogger<WebSearchTool> _logger;

    public WebSearchTool(
        string apiKey,
        string searchEngineId,
        ICacheService? cacheService = null,
        HttpClient? httpClient = null,
        ILogger<WebSearchTool>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchEngineId);

        _apiKey = apiKey;
        _searchEngineId = searchEngineId;
        _cacheService = cacheService;
        _logger = logger ?? NullLogger<WebSearchTool>.Instance;
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

    public string Name => "WebSearch";

    public string Description => "Searches the web using Google Custom Search API and returns curated links with snippets.";

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
        string cacheKey = $"tool:websearch:{normalizedQuery}";

        if (_cacheService is not null)
        {
            var cached = await _cacheService.GetAsync<ToolExecutionResult>(cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null && cached.Success)
            {
                _logger.LogDebug("Returning cached web search result for query '{Query}'.", query);
                return cached;
            }
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
                ["query"] = query
            };

            if (root.TryGetProperty("searchInformation", out JsonElement searchInfo) && searchInfo.TryGetProperty("totalResults", out JsonElement totalResults))
            {
                metadata["totalResults"] = totalResults.GetString() ?? string.Empty;
            }

            var result = new ToolExecutionResult
            {
                ToolName = Name,
                Success = true,
                Output = outputBuilder.Length == 0 ? "No results found." : outputBuilder.ToString().Trim(),
                Citations = citations,
                Metadata = metadata
            };

            if (_cacheService is not null && result.Success)
            {
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Cached web search result for query '{Query}'.", query);
            }

            return result;
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
                    ["query"] = query
                }
            };
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

