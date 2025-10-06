using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Configuration;
using Marconian.ResearchAgent.Models.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class ComputerUseSearchService : IAsyncDisposable, IComputerUseExplorer
{
    private const string ResponsesApiVersion = "2024-08-01-preview";
    private const int MaxIterations = 12;
    private const int MaxContinuationAttempts = 3;
    private const int MaxFlagReminderAttempts = 6;
    private const string ExplorationInstructions = """
You are an expert researcher controlling a browser to find high-quality resources that answer a complex objective. Your goal is not just to browse, but to explore with intent and curiosity.

1.  **Strategize**: Formulate a plan to find definitive, high-quality resources (e.g., articles, papers, datasets). Start with broad searches and progressively narrow your focus.
2.  **Explore**: Navigate, scroll, and follow promising links. Prioritize primary sources and in-depth articles over blogs or forums.
3.  **Flag Resources**: When you discover a resource that directly contributes to the objective, use the `flag_resource` function to mark it. Do not flag search result pages or navigation hubs.
4.  **Persist**: Do not stop at the first relevant link. Continue exploring until you are confident you have found the best possible resources or have exhausted all promising paths.
5.  **Summarize**: Only when your exploration is complete, call `submit_summary` to provide a synthesis of your findings, supported by the resources you flagged. If you get stuck (e.g., paywall, login), note it and conclude.
""";
    private const string ExplorationSummaryPrompt = "You have finished gathering observations. Call the submit_summary function with a concise summary, at least three findings, and any notable resources. Do not perform further computer actions.";
    private const string ExplorationSummaryInstructions = "You have completed browsing. Finish by calling the submit_summary function with the final structured summary.";
    private const string SearchInstructions = """
You are an expert researcher controlling a browser to answer a query. You are currently on a search results page.

1.  **Analyze Results**: Examine the search results on the screen. Identify the most promising organic result that appears to be a direct link to an article or primary source.
2.  **Click the Link**: Execute a `click` action on the most promising link to navigate to the content page. Do NOT use the search bar again.
3.  **Explore and Flag**: Once on a content page, explore it. If it is a high-quality resource, use the `flag_resource` function.
4.  **Summarize**: After exploring and flagging, call `submit_summary` to complete the task.
""";
    private const string ExplorationContinuationPrompt = "Continue exploring the current page or follow promising links to gather more evidence before calling submit_summary.";
    private const string FlagResourceReminderPromptSingle = "You have not flagged any resources yet. Locate a high-value article, dataset, or downloadable file that addresses the objective and call the flag_resource function with its metadata before summarizing.";
    private const string FlagResourceReminderInstructionsSuffixSingle = "Priority: identify the strongest candidate resource (page, PDF, dataset) and call flag_resource with its URL, title, notes, mimeType, and type before invoking submit_summary. Do not conclude without at least one flagged resource.";

    private static readonly Regex FlagCountDigitPattern = new("(?<count>\\d+)\\s+(?:resources?|links?|articles?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FlagCountWordPattern = new("(?<word>one|two|three|four|five|six|seven|eight|nine|ten)\\s+(?:resources?|links?|articles?)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AtLeastDigitPattern = new("at least\\s+(?<count>\\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AtLeastWordPattern = new("at least\\s+(?<word>one|two|three|four|five|six|seven|eight|nine|ten)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, int> FlagNumberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10
    };

    private const string SummaryFunctionName = "submit_summary";
    private const string FlagResourceFunctionName = "flag_resource";
    private const int MinExplorationIterations = 3;

    private readonly ComputerUseOptions _environmentOptions;
    private readonly ComputerUseBrowserFactory _browserFactory;
    private readonly HumanInteractionSimulator _interactionSimulator;
    private ComputerUseBrowserSession? _browserSession;
    private ComputerUseProxySelection? _activeProxy;
    private readonly int _viewportWidth;
    private readonly int _viewportHeight;

    private static readonly JsonObject FlagResourceFunctionDefinition = new()
    {
        ["type"] = "function",
        ["name"] = FlagResourceFunctionName,
        ["description"] = "Register a high-value resource (article, file, download) discovered during exploration. Never call this for search pages or navigation hubs.",
        ["strict"] = true,
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Direct URL to the source resource. Must not be a search or listing page."
                },
                ["title"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "Readable title for the resource."
                },
                ["notes"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "Optional rationale explaining why this resource matters."
                },
                ["mimeType"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "Optional MIME type (for example, application/pdf)."
                },
                ["type"] = new JsonObject
                {
                    ["type"] = new JsonArray("string", "null"),
                    ["description"] = "Resource classification.",
                    ["enum"] = new JsonArray("page", "file", "download")
                }
            },
            ["required"] = new JsonArray("url", "title", "notes", "mimeType", "type")
        }
    };

    private static readonly JsonObject SummaryFunctionDefinition = new()
    {
        ["type"] = "function",
        ["name"] = SummaryFunctionName,
        ["description"] = "Finalize the exploration by returning the structured findings and previously flagged resources.",
        ["strict"] = true,
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["summary"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Concise narrative synthesis of the exploration findings."
                },
                ["findings"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Bullet-style findings extracted from the exploration.",
                    ["minItems"] = 3,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "string"
                    }
                },
                ["flagged"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Resources worth revisiting that were discovered via flag_resource.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["url"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Fully-qualified URL pointing to the resource."
                            },
                            ["title"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null"),
                                ["description"] = "Human-readable title for the resource."
                            },
                            ["notes"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null"),
                                ["description"] = "Optional contextual notes for the resource."
                            },
                            ["mimeType"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null"),
                                ["description"] = "Optional MIME type if known (e.g., application/pdf)."
                            },
                            ["type"] = new JsonObject
                            {
                                ["type"] = new JsonArray("string", "null"),
                                ["description"] = "Resource type classification.",
                                ["enum"] = new JsonArray("page", "file", "download")
                            }
                        },
                        ["required"] = new JsonArray("url", "title", "notes", "mimeType", "type")
                    }
                }
            },
            ["required"] = new JsonArray("summary", "findings", "flagged")
        }
    };

    private static readonly Dictionary<string, string> KeyMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/"] = "Slash",
        ["\\"] = "Backslash",
        ["alt"] = "Alt",
        ["arrowdown"] = "ArrowDown",
        ["arrowleft"] = "ArrowLeft",
        ["arrowright"] = "ArrowRight",
        ["arrowup"] = "ArrowUp",
        ["backspace"] = "Backspace",
        ["ctrl"] = "Control",
        ["control"] = "Control",
        ["delete"] = "Delete",
        ["enter"] = "Enter",
        ["esc"] = "Escape",
        ["escape"] = "Escape",
        ["shift"] = "Shift",
        ["space"] = " ",
        ["tab"] = "Tab",
        ["win"] = "Meta",
        ["cmd"] = "Meta",
        ["super"] = "Meta",
        ["option"] = "Alt"
    };

    private static readonly HashSet<string> ScrollKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ArrowDown",
        "ArrowUp",
        "ArrowLeft",
        "ArrowRight",
        "PageDown",
        "PageUp",
        "End",
        "Home",
        "Space"
    };

    private static readonly string[] ConsentButtonSelectors =
    {
        // Bing specific
        "#bnp_btn_reject",
        "#bnp_btn_decline",
        "button#bnp_btn_reject",
        "button#bnp_btn_decline",
        // Generic reject / decline
        "button[aria-label*='Reject' i]",
        "button[aria-label*='Decline' i]",
        "button:has-text(\"Reject\")",
        "button:has-text(\"Decline\")",
        "button:has-text(\"No thanks\")",
        "button:has-text(\"Not now\")",
        // Google specific (EU consent screens)
        "button:has-text(\"Reject all\")",
        "button[aria-label*='Reject all' i]",
        "button:has-text(\"I do not agree\")",
        // Fallback acceptance (only if reject not present)
        "button:has-text(\"Accept all\")",
        "button[aria-label*='Accept all' i]"
    };

    private static readonly Regex[] ConsentButtonNamePatterns =
    {
        new("reject all", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("reject", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("decline", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("no thanks", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("not now", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("accept all", RegexOptions.IgnoreCase | RegexOptions.Compiled) // fallback if reject unavailable
    };

    private bool _supportsSummaryFunction = true;

    private readonly List<FlaggedResource> _flaggedResources = new();

    private const string ConsentDismissScript = @"() => {
        const selectors = ['#bnp_btn_reject', '#bnp_btn_decline', '[aria-label*=""Reject"" i]', '[aria-label*=""Decline"" i]'];
        for (const selector of selectors) {
            const el = document.querySelector(selector);
            if (el) {
                el.click();
                return true;
            }
        }

        const candidates = Array.from(document.querySelectorAll('button, a, span, div'));
        const target = candidates.find(el => {
            const text = (el.textContent || '').trim().toLowerCase();
            if (!text) {
                return false;
            }

            return text.includes('reject') || text.includes('decline') || text.includes('no thanks') || text.includes('not now');
        });

        if (target) {
            target.dispatchEvent(new MouseEvent('click', { bubbles: true }));
            return true;
        }

        return false;
    }";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ComputerUseTimeoutOptions _timeouts;
    private readonly string _endpoint;
    private readonly string _deployment;
    private readonly string _apiKey;
    private readonly ILogger<ComputerUseSearchService> _logger;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _initialized;
    private string? _lastScreenshot;
    private string? _currentSessionId;
    private int _screenshotSequence;
    private List<TimelineEntry>? _timelineEntries;
    private string? _timelinePath;
    private EventHandler<IPage>? _contextPageHandler;

    public ComputerUseSearchService(
        string endpoint,
        string apiKey,
        string deployment,
        HttpClient httpClient,
        ILogger<ComputerUseSearchService>? logger = null,
        ComputerUseTimeoutOptions? timeouts = null,
        ComputerUseOptions? environmentOptions = null,
        ComputerUseBrowserFactory? browserFactory = null,
        HumanInteractionSimulator? interactionSimulator = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Azure OpenAI endpoint must be supplied.", nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Azure OpenAI API key must be supplied.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            throw new ArgumentException("Computer-use deployment name must be supplied.", nameof(deployment));
        }

        _endpoint = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
        _apiKey = apiKey;
        _deployment = deployment;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<ComputerUseSearchService>.Instance;
        _timeouts = timeouts ?? ComputerUseTimeoutOptions.Default;
        _environmentOptions = environmentOptions ?? new ComputerUseOptions();
        _viewportWidth = Math.Clamp(_environmentOptions.Viewport.Width, 640, 3840);
        _viewportHeight = Math.Clamp(_environmentOptions.Viewport.Height, 480, 2160);
        _interactionSimulator = interactionSimulator ?? new HumanInteractionSimulator(_environmentOptions.Behavior, _viewportWidth, _viewportHeight);
        _browserFactory = browserFactory ?? new ComputerUseBrowserFactory(_environmentOptions, _logger);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            _browserSession = await _browserFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
            _playwright = _browserSession.Playwright;
            _browser = _browserSession.Browser;
            _context = _browserSession.Context;
            _page = _browserSession.Page;
            AttachContextEventHandlers();
            _activeProxy = _browserSession.ProxySelection;
            _interactionSimulator.Reset();
            ApplyDefaultTimeouts();

            string requestedTimeZone = _activeProxy?.Endpoint?.PreferredTimeZoneId ?? _environmentOptions.PreferredTimeZoneId ?? TimeZoneInfo.Local.Id;
            string resolvedTimeZone = _browserSession?.TimeZoneId ?? "UTC";

            if (_activeProxy?.Endpoint is { } endpoint)
            {
                _logger.LogInformation(
                    "Proxy engaged for session via {Proxy} (Locale={Locale}, TimeZone={TimeZone}, RequestedTimeZone={RequestedTimeZone}).",
                    endpoint.Uri,
                    endpoint.PreferredLocale ?? _environmentOptions.PreferredLocale ?? "default",
                    resolvedTimeZone,
                    string.IsNullOrWhiteSpace(requestedTimeZone) ? "(default)" : requestedTimeZone);
            }
            else
            {
                _logger.LogInformation(
                    "No proxy configured for session (Locale={Locale}, TimeZone={TimeZone}, RequestedTimeZone={RequestedTimeZone}).",
                    _environmentOptions.PreferredLocale ?? "default",
                    resolvedTimeZone,
                    string.IsNullOrWhiteSpace(requestedTimeZone) ? "(default)" : requestedTimeZone);
            }

            await _page.GotoAsync("https://www.google.com/ncr", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private void ApplyDefaultTimeouts()
    {
        if (!ShouldApplyTimeout(_timeouts.DefaultActionTimeout) && !ShouldApplyTimeout(_timeouts.NavigationTimeout))
        {
            return;
        }

        if (ShouldApplyTimeout(_timeouts.DefaultActionTimeout))
        {
            float timeout = ClampTimeout(_timeouts.DefaultActionTimeout, 500, 60000);
            _context?.SetDefaultTimeout(timeout);
            _page?.SetDefaultTimeout(timeout);
        }

        if (ShouldApplyTimeout(_timeouts.NavigationTimeout))
        {
            float timeout = ClampTimeout(_timeouts.NavigationTimeout, 1000, 180000);
            _context?.SetDefaultNavigationTimeout(timeout);
            _page?.SetDefaultNavigationTimeout(timeout);
        }
    }

    public async Task<ComputerUseSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        _flaggedResources.Clear();

        CancellationToken effectiveToken = cancellationToken;
        CancellationTokenSource? timeoutCts = null;
        Stopwatch? stopwatch = null;
        bool lockAcquired = false;

        if (ShouldApplyTimeout(_timeouts.SearchOperationTimeout))
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeouts.SearchOperationTimeout);
            effectiveToken = timeoutCts.Token;
            stopwatch = Stopwatch.StartNew();
        }

        try
        {
            await InitializeAsync(effectiveToken).ConfigureAwait(false);
            await _operationLock.WaitAsync(effectiveToken).ConfigureAwait(false);
            lockAcquired = true;

            return await ExecuteSearchCoreAsync(query, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            stopwatch?.Stop();
            await HandleOperationTimeoutAsync("search", _timeouts.SearchOperationTimeout, ex).ConfigureAwait(false);
            throw new ComputerUseOperationTimeoutException($"Computer-use search timed out after {_timeouts.SearchOperationTimeout.TotalSeconds:F0} seconds.", ex);
        }
        finally
        {
            stopwatch?.Stop();

            try
            {
                if (lockAcquired)
                {
                    try
                    {
                        await PersistTimelineAsync(query, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist computer-use timeline for query '{Query}'.", query);
                    }
                }
            }
            finally
            {
                if (lockAcquired)
                {
                    _operationLock.Release();
                }

                timeoutCts?.Dispose();
            }
        }
    }

    private async Task HandleOperationTimeoutAsync(string operation, TimeSpan timeout, Exception cause)
    {
        double seconds = Math.Round(timeout.TotalSeconds, 2);
        _logger.LogWarning(cause, "Computer-use {Operation} timed out after {TimeoutSeconds} seconds. Resetting Playwright session.", operation, seconds);
        RecordTimelineEvent("operation_timeout", new Dictionary<string, object?>
        {
            ["operation"] = operation,
            ["timeoutSeconds"] = seconds
        });
        await ResetBrowserSessionAsync().ConfigureAwait(false);
    }

    private async Task<ComputerUseSearchResult> ExecuteSearchCoreAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            _screenshotSequence = 0;
            InitializeTimeline(query);
            if (_page is null)
            {
                throw new InvalidOperationException("Computer-use browser page was not initialized.");
            }

            await NavigateToStartAsync(cancellationToken).ConfigureAwait(false);
            var transcript = new List<string>();
            var acknowledgedFlaggedCallIds = new HashSet<string>(StringComparer.Ordinal);
            string? lastResponseId = null;

            ComputerUseResponseState response = await SendInitialRequestAsync(query, transcript, cancellationToken).ConfigureAwait(false);
            lastResponseId = response.ResponseId;
            (response, lastResponseId) = await HandleFlaggedFunctionCallsAsync(response, transcript, acknowledgedFlaggedCallIds, lastResponseId, cancellationToken).ConfigureAwait(false);
            int iteration = 0;

            while (response.Call is not null && iteration < MaxIterations)
            {
                iteration++;
                await HandleComputerCallAsync(response.Call, cancellationToken).ConfigureAwait(false);
                response = await SendFollowupRequestAsync(response, transcript, cancellationToken, SearchInstructions).ConfigureAwait(false);
                (response, lastResponseId) = await HandleFlaggedFunctionCallsAsync(response, transcript, acknowledgedFlaggedCallIds, lastResponseId, cancellationToken).ConfigureAwait(false);

                if (response.Call is null || response.Completed)
                {
                    break;
                }
            }

            if (iteration >= MaxIterations && response.Call is not null)
            {
                _logger.LogWarning("Computer-use search for query '{Query}' reached iteration limit before completion.", query);
            }

            (response, lastResponseId) = await HandleFlaggedFunctionCallsAsync(response, transcript, acknowledgedFlaggedCallIds, lastResponseId, cancellationToken).ConfigureAwait(false);
            await EnsureSearchResultsAsync(query, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ComputerUseSearchResultItem> items = await ExtractResultsAsync(query, transcript, cancellationToken).ConfigureAwait(false);
            string? finalUrl = _page.Url;
            return new ComputerUseSearchResult(items, transcript.ToArray(), finalUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Computer-use search failed for query '{Query}'.", query);
            throw;
        }
    }

    public async Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        _flaggedResources.Clear();

        CancellationToken originalToken = cancellationToken;
        CancellationToken effectiveToken = cancellationToken;
        CancellationTokenSource? timeoutCts = null;
        Stopwatch? stopwatch = null;
        bool lockAcquired = false;

        if (ShouldApplyTimeout(_timeouts.ExplorationOperationTimeout))
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_timeouts.ExplorationOperationTimeout);
            effectiveToken = timeoutCts.Token;
            stopwatch = Stopwatch.StartNew();
        }

        try
        {
            await InitializeAsync(effectiveToken).ConfigureAwait(false);
            await _operationLock.WaitAsync(effectiveToken).ConfigureAwait(false);
            lockAcquired = true;

            return await ExecuteExplorationCoreAsync(url, objective, effectiveToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !originalToken.IsCancellationRequested)
        {
            stopwatch?.Stop();
            await HandleOperationTimeoutAsync("exploration", _timeouts.ExplorationOperationTimeout, ex).ConfigureAwait(false);
            throw new ComputerUseOperationTimeoutException($"Computer-use exploration timed out after {_timeouts.ExplorationOperationTimeout.TotalSeconds:F0} seconds.", ex);
        }
        finally
        {
            stopwatch?.Stop();

            try
            {
                if (lockAcquired)
                {
                    try
                    {
                        await PersistTimelineAsync(url, originalToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to persist computer-use timeline for exploration '{Url}'.", url);
                    }

                    try
                    {
                        await NavigateToStartAsync(originalToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to reset browser after exploration session.");
                    }
                }
            }
            finally
            {
                if (lockAcquired)
                {
                    _operationLock.Release();
                }

                timeoutCts?.Dispose();
            }
        }
    }

    private async Task NavigateToStartAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        await _page.GotoAsync("https://www.google.com/ncr", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 20000
        }).ConfigureAwait(false);

        await PreparePageForComputerUseAsync(cancellationToken).ConfigureAwait(false);
        await _page.BringToFrontAsync().ConfigureAwait(false);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ComputerUseResponseState> SendInitialRequestAsync(string query, List<string> transcript, CancellationToken cancellationToken)
    {
        await PreparePageForComputerUseAsync(cancellationToken).ConfigureAwait(false);
        string screenshot = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = $"Use Google Search to gather the top five organic results for \"{query}\". Avoid ads and sponsored listings."
                        },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:image/png;base64,{screenshot}"
                        }
                    }
                }
            },
            ["instructions"] = SearchInstructions,
            ["tools"] = BuildToolsArray(),
            ["reasoning"] = new JsonObject { ["generate_summary"] = "concise" },
            ["temperature"] = 0.2,
            ["top_p"] = 0.8,
            ["truncation"] = "auto"
        };
        payload["model"] = _deployment;

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private async Task<ComputerUseResponseState> SendFollowupRequestAsync(ComputerUseResponseState previous, List<string> transcript, CancellationToken cancellationToken, string? instructions = null)
    {
        if (previous.Call is null)
        {
            return previous;
        }

        if (string.IsNullOrWhiteSpace(previous.Call.CallId))
        {
            throw new InvalidOperationException("Computer-use response did not include a call identifier.");
        }

        string screenshot = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        var output = new JsonObject
        {
            ["type"] = "computer_call_output",
            ["call_id"] = previous.Call.CallId,
            ["output"] = new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:image/png;base64,{screenshot}"
            }
        };

        if (previous.Call.PendingSafetyChecks.Count > 0)
        {
            var acknowledged = new JsonArray();
            foreach (var check in previous.Call.PendingSafetyChecks)
            {
                acknowledged.Add(new JsonObject
                {
                    ["id"] = check.Id,
                    ["code"] = check.Code,
                    ["message"] = check.Message
                });
            }

            output["acknowledged_safety_checks"] = acknowledged;
        }

        if (_page is not null)
        {
            string? currentUrl = _page.Url;
            if (!string.IsNullOrWhiteSpace(currentUrl) && !string.Equals(currentUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                output["current_url"] = currentUrl;
            }
        }

        var payload = new JsonObject
        {
            ["input"] = new JsonArray { output },
            ["tools"] = BuildToolsArray(),
            ["previous_response_id"] = previous.ResponseId,
            ["truncation"] = "auto"
        };
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            payload["instructions"] = instructions;
        }
        payload["model"] = _deployment;

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            string? urlForLog = null;
            if (output.TryGetPropertyValue("current_url", out JsonNode? urlNode) && urlNode is JsonValue urlValue)
            {
                urlForLog = urlValue.GetValue<string>();
            }

            _logger.LogDebug(
                "Submitting computer_call_output (callId={CallId}, responseId={ResponseId}, url={Url})",
                previous.Call.CallId,
                previous.ResponseId,
                urlForLog);
        }

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private async Task<ComputerUseResponseState> SendSummaryFunctionOutputAsync(
        ComputerUseResponseState previous,
        List<string> transcript,
        string callId,
        string? rawJsonPayload,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previous.ResponseId))
        {
            return previous;
        }

        string? outputValue = rawJsonPayload;
        if (string.IsNullOrWhiteSpace(outputValue))
        {
            if (previous.SummaryPayload is not null)
            {
                outputValue = JsonSerializer.Serialize(previous.SummaryPayload, SerializerOptions);
            }
            else
            {
                outputValue = "{\"status\":\"received\"}";
            }
        }

        string finalOutputValue = outputValue!;

        var payload = new JsonObject
        {
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = callId,
                    ["output"] = finalOutputValue
                }
            },
            ["tools"] = BuildToolsArray(),
            ["previous_response_id"] = previous.ResponseId,
            ["truncation"] = "auto",
            ["model"] = _deployment
        };

        _logger.LogInformation("Submitting function_call_output for {FunctionName} (callId={CallId}).", SummaryFunctionName, callId);
        RecordTimelineEvent("summary_acknowledged", new Dictionary<string, object?>
        {
            ["callId"] = callId,
            ["payloadLength"] = finalOutputValue.Length
        });

        try
        {
            using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
            return ParseResponse(response, transcript);
        }
        catch (ComputerUseSummaryFunctionException ex)
        {
            _supportsSummaryFunction = false;
            _logger.LogWarning(ex, "Summary function disabled after acknowledgement failure; falling back to transcript-only summarization.");
            return previous;
        }
    }

    private async Task<ComputerUseResponseState> SendFlagResourceAcknowledgementAsync(
        ComputerUseResponseState previous,
        List<string> transcript,
        IReadOnlyList<string> callIds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previous.ResponseId) || callIds.Count == 0)
        {
            return previous;
        }

        const string AckPayload = "{\"status\":\"recorded\"}";
        var inputArray = new JsonArray();

        foreach (string callId in callIds)
        {
            if (string.IsNullOrWhiteSpace(callId))
            {
                continue;
            }

            inputArray.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = callId,
                ["output"] = AckPayload
            });

            RecordTimelineEvent("flag_resource_acknowledged", new Dictionary<string, object?>
            {
                ["callId"] = callId,
                ["payloadLength"] = AckPayload.Length
            });
        }

        if (inputArray.Count == 0)
        {
            return previous;
        }

        var payload = new JsonObject
        {
            ["input"] = inputArray,
            ["tools"] = BuildToolsArray(),
            ["previous_response_id"] = previous.ResponseId,
            ["truncation"] = "auto",
            ["model"] = _deployment
        };

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private async Task<ComputerUseResponseState> RequestContinuationAsync(
        ComputerUseResponseState previous,
        List<string> transcript,
        string continuationPrompt,
        string instructions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(previous.ResponseId))
        {
            return previous;
        }

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = continuationPrompt
            }
        };

        var payload = new JsonObject
        {
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            },
            ["tools"] = BuildToolsArray(),
            ["previous_response_id"] = previous.ResponseId,
            ["instructions"] = instructions,
            ["truncation"] = "auto",
            ["model"] = _deployment
        };

        _logger.LogDebug("Requesting additional computer-use actions (responseId={ResponseId}).", previous.ResponseId);
        RecordTimelineEvent("continuation_prompt", new Dictionary<string, object?>
        {
            ["responseId"] = previous.ResponseId,
            ["message"] = continuationPrompt
        });

        try
        {
            using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
            return ParseResponse(response, transcript);
        }
        catch (ComputerUseSummaryFunctionException ex) when (_supportsSummaryFunction)
        {
            _supportsSummaryFunction = false;
            _logger.LogWarning(ex, "Summary function disabled after unsupported response; falling back to transcript-only summarization.");
            return previous;
        }
    }

    private JsonArray BuildToolsArray()
    {
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "computer_use_preview",
                ["display_width"] = _viewportWidth,
                ["display_height"] = _viewportHeight,
                ["environment"] = "browser"
            }
        };

        if (_supportsSummaryFunction)
        {
            tools.Add(SummaryFunctionDefinition.DeepClone());
        }

        tools.Add(FlagResourceFunctionDefinition.DeepClone());

        return tools;
    }

    private async Task<(ComputerUseResponseState State, string? LastResponseId)> HandleFlaggedFunctionCallsAsync(
        ComputerUseResponseState state,
        List<string> transcript,
        HashSet<string> acknowledgedCallIds,
        string? lastResponseId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(state.ResponseId))
        {
            lastResponseId = state.ResponseId;
        }

        if (state.FlaggedFunctionCallIds is null || state.FlaggedFunctionCallIds.Count == 0)
        {
            return (state, lastResponseId);
        }

        string? responseId = !string.IsNullOrWhiteSpace(state.ResponseId) ? state.ResponseId : lastResponseId;
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return (state, lastResponseId);
        }

        var callIdsToAcknowledge = new List<string>();
        foreach (string callId in state.FlaggedFunctionCallIds)
        {
            if (string.IsNullOrWhiteSpace(callId))
            {
                continue;
            }

            if (acknowledgedCallIds.Add(callId))
            {
                callIdsToAcknowledge.Add(callId);
            }
        }

        if (callIdsToAcknowledge.Count == 0)
        {
            return (state, lastResponseId);
        }

        var ackState = state with { ResponseId = responseId };
        ComputerUseResponseState current = await SendFlagResourceAcknowledgementAsync(ackState, transcript, callIdsToAcknowledge, cancellationToken).ConfigureAwait(false);

        string? currentLastResponseId = !string.IsNullOrWhiteSpace(current.ResponseId) ? current.ResponseId : responseId;

        if (current.FlaggedFunctionCallIds is { Count: > 0 })
        {
            return await HandleFlaggedFunctionCallsAsync(current, transcript, acknowledgedCallIds, currentLastResponseId, cancellationToken).ConfigureAwait(false);
        }

        return (current, currentLastResponseId);
    }

    private async Task<JsonDocument> SendRequestAsync(JsonNode payload, CancellationToken cancellationToken)
    {
        string body = payload.ToJsonString(SerializerOptions);

        var endpoints = new (string Uri, bool AppendApiVersion, string Tag)[]
        {
            ($"{_endpoint}openai/v1/responses", false, "unified"),
            ($"{_endpoint}openai/deployments/{_deployment}/responses", true, "deployment")
        };

        for (int index = 0; index < endpoints.Length; index++)
        {
            (string baseUri, bool appendApiVersion, string tag) = endpoints[index];
            string uri = appendApiVersion ? $"{baseUri}?api-version={ResponsesApiVersion}" : baseUri;
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("api-key", _apiKey);

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (index > 0)
                {
                    _logger.LogInformation("Computer-use request succeeded after falling back to {Tag} endpoint.", tag);
                }

                return JsonDocument.Parse(responseContent);
            }

            bool canRetry = index < endpoints.Length - 1 &&
                (response.StatusCode == HttpStatusCode.NotFound ||
                 (response.StatusCode == HttpStatusCode.BadRequest && responseContent.Contains("API version not supported", StringComparison.OrdinalIgnoreCase)));

            _logger.LogWarning("Computer-use responses call to {Tag} endpoint failed with status {Status}: {Body}", tag, response.StatusCode, responseContent);

            if (!canRetry)
            {
                bool summaryFunctionIssue = response.StatusCode == HttpStatusCode.BadRequest &&
                    (responseContent.Contains("\"text.format\"", StringComparison.OrdinalIgnoreCase) ||
                     responseContent.Contains("computer_use_summary", StringComparison.OrdinalIgnoreCase) ||
                     responseContent.Contains(SummaryFunctionName, StringComparison.OrdinalIgnoreCase) ||
                     responseContent.Contains("\"type\":\"function\"", StringComparison.OrdinalIgnoreCase));

                if (summaryFunctionIssue)
                {
                    throw new ComputerUseSummaryFunctionException(responseContent);
                }

                response.EnsureSuccessStatusCode();
            }
        }

        throw new InvalidOperationException("Computer-use request failed for all configured endpoints.");
    }

    private ComputerUseResponseState ParseResponse(JsonDocument document, List<string> transcript)
    {
        JsonElement root = document.RootElement;
        string? responseId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        string? status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        bool completed = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

        ComputerCall? call = null;
        ExplorationPayload? summaryPayload = null;
        string? summaryRawJson = null;
        string? summaryCallId = null;
        string? summaryResponseId = null;
        List<string>? capturedSegments = null;
        List<string>? flaggedFunctionCallIds = null;

        if (root.TryGetProperty("output", out JsonElement outputElementForLogging) && outputElementForLogging.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in outputElementForLogging.EnumerateArray())
            {
                try
                {
                    RecordTimelineEvent("model_tool_call", new Dictionary<string, object?>
                    {
                        ["raw"] = ParseTimelinePayload(item)
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to capture raw tool call for timeline entry.");
                }
            }
        }


        void ProcessToolCandidate(JsonElement candidate)
        {
            try
            {
                RecordTimelineEvent("model_tool_call", new Dictionary<string, object?>
                {
                    ["raw"] = ParseTimelinePayload(candidate)
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to capture raw tool call for timeline entry.");
            }

            if (TryExtractSummaryFunction(candidate, out ExplorationPayload? payload, out string? rawJson, out string? callId))
            {
                summaryPayload = payload;
                summaryRawJson = rawJson;
                if (!string.IsNullOrWhiteSpace(callId))
                {
                    summaryCallId = callId;
                    summaryResponseId = responseId;
                }
            }

            if (TryExtractFlagResourceFunction(candidate, out FlaggedResource resource, out string? flagRaw, out string? flagCallId))
            {
                RegisterFlaggedResource(resource, flagRaw);
                if (!string.IsNullOrWhiteSpace(flagCallId))
                {
                    (flaggedFunctionCallIds ??= new List<string>()).Add(flagCallId);
                }
            }
        }

        if (root.TryGetProperty("output", out JsonElement outputElement) && outputElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in outputElement.EnumerateArray())
            {
                string? type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    string? text = item.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string trimmed = text.Trim();
                        transcript.Add(trimmed);
                        (capturedSegments ??= new List<string>()).Add(trimmed);
                    }
                }
                else if (string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
                {
                    string? text = item.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string trimmed = text.Trim();
                        transcript.Add(trimmed);
                        (capturedSegments ??= new List<string>()).Add(trimmed);
                    }
                }
                else if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement content in contentElement.EnumerateArray())
                        {
                            string? contentType = content.TryGetProperty("type", out var ct) ? ct.GetString() : null;
                            if (string.Equals(contentType, "text", StringComparison.OrdinalIgnoreCase))
                            {
                                string? text = content.TryGetProperty("text", out var contentText) ? contentText.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    string trimmed = text.Trim();
                                    transcript.Add(trimmed);
                                    (capturedSegments ??= new List<string>()).Add(trimmed);
                                }
                            }
                            else if (string.Equals(contentType, "reasoning", StringComparison.OrdinalIgnoreCase))
                            {
                                if (content.TryGetProperty("text", out var reasoningText) && reasoningText.ValueKind == JsonValueKind.String)
                                {
                                    string? reasoning = reasoningText.GetString();
                                    if (!string.IsNullOrWhiteSpace(reasoning))
                                    {
                                        string trimmed = reasoning.Trim();
                                        transcript.Add(trimmed);
                                        (capturedSegments ??= new List<string>()).Add(trimmed);
                                    }
                                }
                                else if (content.TryGetProperty("summary", out var reasoningSummary) && reasoningSummary.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (JsonElement reason in reasoningSummary.EnumerateArray())
                                    {
                                        string? reasoning = reason.ValueKind switch
                                        {
                                            JsonValueKind.String => reason.GetString(),
                                            JsonValueKind.Object when reason.TryGetProperty("text", out var reasonText) => reasonText.GetString(),
                                            _ => null
                                        };

                                        if (!string.IsNullOrWhiteSpace(reasoning))
                                        {
                                            string trimmed = reasoning.Trim();
                                            transcript.Add(trimmed);
                                            (capturedSegments ??= new List<string>()).Add(trimmed);
                                        }
                                    }
                                }
                            }

                            ProcessToolCandidate(content);
                        }
                    }

                    ProcessToolCandidate(item);
                }
                else if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement summary in summaryElement.EnumerateArray())
                        {
                            string? reasoningText = summary.ValueKind switch
                            {
                                JsonValueKind.String => summary.GetString(),
                                JsonValueKind.Object when summary.TryGetProperty("text", out var summaryText) => summaryText.GetString(),
                                _ => null
                            };

                            if (!string.IsNullOrWhiteSpace(reasoningText))
                            {
                                transcript.Add(reasoningText.Trim());
                                (capturedSegments ??= new List<string>()).Add(reasoningText.Trim());
                            }
                        }
                    }
                }
                else if (string.Equals(type, "computer_call", StringComparison.OrdinalIgnoreCase))
                {
                    string? callId = item.TryGetProperty("call_id", out var callIdElement) ? callIdElement.GetString() : null;
                    if (item.TryGetProperty("action", out var actionElement))
                    {
                        ComputerUseAction? action = ParseAction(actionElement);
                        if (!string.IsNullOrWhiteSpace(callId) && action is not null)
                        {
                            var safetyChecks = new List<ComputerUseSafetyCheck>();
                            if (item.TryGetProperty("pending_safety_checks", out var safetyElement) && safetyElement.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement check in safetyElement.EnumerateArray())
                                {
                                    string? id = check.TryGetProperty("id", out var sid) ? sid.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(id))
                                    {
                                        continue;
                                    }

                                    string? code = check.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                                    string? message = check.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
                                    safetyChecks.Add(new ComputerUseSafetyCheck(id, code ?? string.Empty, message ?? string.Empty));
                                }
                            }

                            call = new ComputerCall(callId, action, safetyChecks);
                        }
                    }
                }
                else
                {
                    ProcessToolCandidate(item);
                }
            }
        }

        if (capturedSegments is not null && capturedSegments.Count > 0)
        {
            RecordTimelineEvent("model_text", new Dictionary<string, object?>
            {
                ["count"] = capturedSegments.Count,
                ["segments"] = capturedSegments.ToArray()
            });

            foreach (string snippet in capturedSegments.Select(segment => segment.Length > 512 ? segment[..512] + "…" : segment))
            {
                _logger.LogDebug("Computer-use model text: {Snippet}", snippet);
            }
        }

        if (summaryPayload is not null)
        {
            int summaryFlaggedCount = summaryPayload.Flagged?.Count ?? 0;
            int toolFlaggedCount = _flaggedResources.Count;

            var distinctUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (summaryPayload.Flagged is not null)
            {
                foreach (ExplorationPayloadResource? resource in summaryPayload.Flagged)
                {
                    string? url = resource?.Url;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        distinctUrls.Add(url.Trim());
                    }
                }
            }

            foreach (FlaggedResource resource in _flaggedResources)
            {
                distinctUrls.Add(resource.Url);
            }

            int totalFlaggedCount = distinctUrls.Count;

            var timelinePayload = new Dictionary<string, object?>
            {
                ["summary"] = summaryPayload.Summary,
                ["findings"] = summaryPayload.Findings?.Count ?? 0,
                ["flagged"] = totalFlaggedCount,
                ["summaryFlagged"] = summaryFlaggedCount,
                ["toolFlagged"] = toolFlaggedCount,
                ["raw"] = summaryRawJson
            };

            RecordTimelineEvent("submit_summary", timelinePayload);

            _logger.LogInformation(
                "Captured submit_summary payload with {FindingsCount} findings, {SummaryFlaggedCount} summary resources, {ToolFlaggedCount} tool-flagged resources (total distinct {TotalFlaggedCount}).",
                summaryPayload.Findings?.Count ?? 0,
                summaryFlaggedCount,
                toolFlaggedCount,
                totalFlaggedCount);
        }

        return new ComputerUseResponseState(responseId, call, completed, summaryCallId)
        {
            SummaryPayload = summaryPayload,
            SummaryRawJson = summaryRawJson,
            SummaryResponseId = summaryResponseId,
            FlaggedFunctionCallIds = flaggedFunctionCallIds is { Count: > 0 } ? flaggedFunctionCallIds.ToArray() : null
        };
    }

    private object? ParseTimelinePayload(JsonElement element)
    {
        string raw = element.GetRawText();

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            _logger.LogDebug(ex, "Failed to parse model tool call payload for timeline entry.");
            return raw;
        }
    }

    private bool TryExtractSummaryFunction(JsonElement element, out ExplorationPayload? payload, out string? rawJson, out string? callId)
    {
        payload = null;
        rawJson = null;
        callId = null;

        if (!TryExtractFunctionArguments(element, SummaryFunctionName, out string? rawArguments, out string? callIdCandidate))
        {
            return false;
        }

        try
        {
            ExplorationPayload? parsed = JsonSerializer.Deserialize<ExplorationPayload>(rawArguments!, SerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            payload = parsed;
            rawJson = rawArguments;
            callId = callIdCandidate;
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse submit_summary payload: {Payload}", rawArguments);
            return false;
        }
    }

    private bool TryExtractFlagResourceFunction(JsonElement element, out FlaggedResource resource, out string? rawJson, out string? callId)
    {
        resource = null!;
        rawJson = null;
        callId = null;

        if (!TryExtractFunctionArguments(element, FlagResourceFunctionName, out string? rawArguments, out string? callIdCandidate))
        {
            return false;
        }

        try
        {
            ExplorationPayloadResource? parsed = JsonSerializer.Deserialize<ExplorationPayloadResource>(rawArguments!, SerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            FlaggedResource? converted = TryConvertResource(parsed);
            if (converted is null)
            {
                return false;
            }

            resource = converted;
            rawJson = rawArguments;
            callId = callIdCandidate;
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse flag_resource payload: {Payload}", rawArguments);
            return false;
        }
    }

    private void RegisterFlaggedResource(FlaggedResource resource, string? rawJson)
    {
        int existingIndex = _flaggedResources.FindIndex(existing => string.Equals(existing.Url, resource.Url, StringComparison.OrdinalIgnoreCase));
        bool isNew = existingIndex < 0;

        if (isNew)
        {
            _flaggedResources.Add(resource);
        }
        else
        {
            FlaggedResource merged = MergeFlaggedResource(_flaggedResources[existingIndex], resource);
            _flaggedResources[existingIndex] = merged;
            resource = merged;
        }

        var metadata = new Dictionary<string, object?>
        {
            ["url"] = resource.Url,
            ["title"] = resource.Title,
            ["type"] = resource.Type.ToString(),
            ["new"] = isNew
        };

        if (!string.IsNullOrWhiteSpace(resource.MimeType))
        {
            metadata["mimeType"] = resource.MimeType;
        }

        if (!string.IsNullOrWhiteSpace(resource.Notes))
        {
            metadata["notes"] = resource.Notes;
        }

        if (!string.IsNullOrWhiteSpace(rawJson))
        {
            object? rawValue = rawJson;

            try
            {
                JsonNode? parsedNode = JsonNode.Parse(rawJson);
                if (parsedNode is not null)
                {
                    rawValue = parsedNode;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse flag_resource raw payload for timeline entry.");
            }

            metadata["raw"] = rawValue;
        }

        RecordTimelineEvent("flag_resource", metadata);

        if (isNew)
        {
            _logger.LogInformation("Captured flagged resource {Url} ({Type}) via flag_resource.", resource.Url, resource.Type);
        }
        else
        {
            _logger.LogInformation("Updated flagged resource {Url} ({Type}) via flag_resource.", resource.Url, resource.Type);
        }
    }

    private bool TryAutoFlagFinalPage(string? finalUrl, string? pageTitle, string? objective, IReadOnlyList<string> transcript)
    {
        if (string.IsNullOrWhiteSpace(finalUrl))
        {
            return false;
        }

        string? normalizedUrl = NormalizeNavigableUrl(finalUrl);
        if (string.IsNullOrWhiteSpace(normalizedUrl) || LooksLikeSearchHub(normalizedUrl))
        {
            return false;
        }

        string title = string.IsNullOrWhiteSpace(pageTitle) ? normalizedUrl : pageTitle!.Trim();
        string? notes = BuildAutoFlagNotes(objective, transcript);
        string? mimeType = LooksLikePdf(normalizedUrl, null) ? "application/pdf" : null;

        var resource = new FlaggedResource(FlaggedResourceType.Page, title, normalizedUrl, mimeType, notes);
        RegisterFlaggedResource(resource, null);

        RecordTimelineEvent("flag_resource_auto", new Dictionary<string, object?>
        {
            ["url"] = resource.Url,
            ["title"] = resource.Title,
            ["objective"] = objective,
            ["notes"] = resource.Notes
        });

        _logger.LogInformation("Auto-flagged final page {Url} ({Title}) after exploration produced no flagged resources.", resource.Url, resource.Title);
        return true;
    }

    private static string? BuildAutoFlagNotes(string? objective, IReadOnlyList<string> transcript)
    {
        string? trimmedObjective = string.IsNullOrWhiteSpace(objective) ? null : objective.Trim();
        string? insight = null;

        for (int index = transcript.Count - 1; index >= 0; index--)
        {
            string segment = transcript[index];
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            string trimmed = segment.Trim();
            if (trimmed.Length < 16)
            {
                continue;
            }

            insight = trimmed.Length > 180 ? trimmed[..180] + "…" : trimmed;
            break;
        }

        if (trimmedObjective is null && insight is null)
        {
            return null;
        }

        if (trimmedObjective is null)
        {
            return insight;
        }

        if (insight is null)
        {
            return $"Objective: {trimmedObjective}.";
        }

        return $"Objective: {trimmedObjective}. {insight}";
    }

    private static int ResolveRequiredFlaggedResourceCount(string? objective)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            return 1;
        }

        string lowered = objective.ToLowerInvariant();
        int? candidate = null;

        foreach (Match match in FlagCountDigitPattern.Matches(lowered))
        {
            if (int.TryParse(match.Groups["count"].Value, out int parsed) && parsed > 0)
            {
                candidate = candidate is null ? parsed : Math.Max(candidate.Value, parsed);
            }
        }

        foreach (Match match in FlagCountWordPattern.Matches(lowered))
        {
            string word = match.Groups["word"].Value;
            if (FlagNumberWords.TryGetValue(word, out int parsed) && parsed > 0)
            {
                candidate = candidate is null ? parsed : Math.Max(candidate.Value, parsed);
            }
        }

        foreach (Match match in AtLeastDigitPattern.Matches(lowered))
        {
            if (int.TryParse(match.Groups["count"].Value, out int parsed) && parsed > 0)
            {
                candidate = candidate is null ? parsed : Math.Max(candidate.Value, parsed);
            }
        }

        foreach (Match match in AtLeastWordPattern.Matches(lowered))
        {
            string word = match.Groups["word"].Value;
            if (FlagNumberWords.TryGetValue(word, out int parsed) && parsed > 0)
            {
                candidate = candidate is null ? parsed : Math.Max(candidate.Value, parsed);
            }
        }

        if (candidate is null && lowered.Contains("flag", StringComparison.OrdinalIgnoreCase) && lowered.Contains("multiple", StringComparison.OrdinalIgnoreCase))
        {
            candidate = 2;
        }

        int resolved = candidate ?? 1;
        return Math.Clamp(resolved, 1, 10);
    }

    private static string BuildFlagRequirementDirective(int requiredCount)
    {
        if (requiredCount <= 1)
        {
            return "Always call the flag_resource function to capture each high-value resource before summarizing.";
        }

        return $"Requirement: Flag at least {requiredCount} distinct high-value resources using the flag_resource function before summarizing. Continue exploring until this requirement is satisfied.";
    }

    private static string BuildFlagReminderPrompt(int requiredCount, int currentCount)
    {
        if (requiredCount <= 1)
        {
            return FlagResourceReminderPromptSingle;
        }

        if (currentCount <= 0)
        {
            return $"You have not flagged any resources yet. Flag at least {requiredCount} distinct high-value resources using the flag_resource function before summarizing.";
        }

        return $"You have only flagged {currentCount} resource(s). Flag at least {requiredCount} distinct high-value resources using the flag_resource function before summarizing.";
    }

    private static string BuildFlagReminderInstructionsSuffix(int requiredCount)
    {
        if (requiredCount <= 1)
        {
            return FlagResourceReminderInstructionsSuffixSingle;
        }

        return $"Priority: reach the minimum requirement of {requiredCount} distinct resources flagged via flag_resource (URL, title, notes, mimeType, type) before invoking submit_summary. Do not conclude until this requirement is satisfied.";
    }

    private int GetDistinctFlaggedResourceCount(ExplorationPayload? summaryPayload)
    {
        var distinctUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FlaggedResource resource in _flaggedResources)
        {
            if (!string.IsNullOrWhiteSpace(resource.Url))
            {
                distinctUrls.Add(resource.Url);
            }
        }

        if (summaryPayload?.Flagged is { Count: > 0 })
        {
            foreach (ExplorationPayloadResource? resource in summaryPayload.Flagged)
            {
                if (resource is null)
                {
                    continue;
                }

                FlaggedResource? converted = TryConvertResource(resource);
                if (converted is not null && !string.IsNullOrWhiteSpace(converted.Url))
                {
                    distinctUrls.Add(converted.Url);
                }
            }
        }

        return distinctUrls.Count;
    }

    private static bool TryExtractFunctionArguments(JsonElement element, string functionName, out string? rawArguments, out string? callId)
    {
        rawArguments = null;
        callId = null;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? name = null;
        JsonElement? argumentsElement = null;
        string? callIdCandidate = ResolveFunctionCallId(element);

        if (element.TryGetProperty("name", out var directName) && directName.ValueKind == JsonValueKind.String)
        {
            name = directName.GetString();
        }

        if (element.TryGetProperty("arguments", out var directArgs) && directArgs.ValueKind != JsonValueKind.Undefined)
        {
            argumentsElement = directArgs;
        }

        if (argumentsElement is null && element.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(name) && functionElement.TryGetProperty("name", out var functionNameElement) && functionNameElement.ValueKind == JsonValueKind.String)
            {
                name = functionNameElement.GetString();
            }

            if (functionElement.TryGetProperty("arguments", out var functionArgs) && functionArgs.ValueKind != JsonValueKind.Undefined)
            {
                argumentsElement = functionArgs;
            }

            callIdCandidate ??= ResolveFunctionCallId(functionElement);
        }

        if (argumentsElement is null && element.TryGetProperty("tool", out var toolElement) && toolElement.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(name) && toolElement.TryGetProperty("name", out var toolName) && toolName.ValueKind == JsonValueKind.String)
            {
                name = toolName.GetString();
            }

            if (toolElement.TryGetProperty("arguments", out var toolArgs) && toolArgs.ValueKind != JsonValueKind.Undefined)
            {
                argumentsElement = toolArgs;
            }

            callIdCandidate ??= ResolveFunctionCallId(toolElement);
        }

        if (!string.Equals(name, functionName, StringComparison.OrdinalIgnoreCase) || argumentsElement is null)
        {
            return false;
        }

        rawArguments = argumentsElement.Value.ValueKind switch
        {
            JsonValueKind.String => argumentsElement.Value.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => argumentsElement.Value.GetRawText(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(rawArguments))
        {
            rawArguments = null;
            return false;
        }

        callId = callIdCandidate;
        return true;
    }

    private static string? ResolveFunctionCallId(JsonElement candidate)
    {
        if (candidate.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (candidate.TryGetProperty("call_id", out var callId) && callId.ValueKind == JsonValueKind.String)
        {
            return callId.GetString();
        }

        if (candidate.TryGetProperty("tool_call_id", out var toolCallId) && toolCallId.ValueKind == JsonValueKind.String)
        {
            return toolCallId.GetString();
        }

        if (candidate.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            return idElement.GetString();
        }

        if (candidate.TryGetProperty("callId", out var camelCase) && camelCase.ValueKind == JsonValueKind.String)
        {
            return camelCase.GetString();
        }

        return null;
    }

    private static ComputerUseAction? ParseAction(JsonElement element)
    {
        string? type = element.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        double? ReadDouble(string property)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.Number)
            {
                return value.GetDouble();
            }

            return null;
        }

        IReadOnlyList<string> keys = Array.Empty<string>();
        if (element.TryGetProperty("keys", out var keysElement) && keysElement.ValueKind == JsonValueKind.Array)
        {
            keys = keysElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .ToArray();
        }

        string? text = element.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        string? button = element.TryGetProperty("button", out var buttonElement) ? buttonElement.GetString() : null;

        return new ComputerUseAction(
            type,
            ReadDouble("x"),
            ReadDouble("y"),
            button,
            ReadDouble("scroll_x") ?? 0,
            ReadDouble("scroll_y") ?? 0,
            keys,
            text,
            ReadDouble("ms")
        );
    }

    private async Task HandleComputerCallAsync(ComputerCall call, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            throw new InvalidOperationException("Computer-use page is unavailable.");
        }

        ComputerUseAction action = call.Action;
        (float X, float Y)? coordinates = ClampCoordinates(action.X, action.Y);
        string normalizedType = action.Type.ToLowerInvariant();

        _logger.LogInformation("Action {Type} | Coords=({X},{Y}) | Button={Button} | Keys={Keys} | Text='{Text}' | Session={Session}",
            normalizedType,
            coordinates?.X.ToString("F0") ?? "-",
            coordinates?.Y.ToString("F0") ?? "-",
            action.Button ?? string.Empty,
            action.Keys.Count == 0 ? "-" : string.Join('+', action.Keys),
            TruncateForLog(action.Text),
            _currentSessionId ?? "-" );

        RecordTimelineEvent("action", new Dictionary<string, object?>
        {
            ["type"] = normalizedType,
            ["x"] = coordinates?.X,
            ["y"] = coordinates?.Y,
            ["button"] = action.Button,
            ["keys"] = action.Keys.Count == 0 ? null : string.Join('+', action.Keys),
            ["text"] = TruncateForLog(action.Text)
        });

        switch (normalizedType)
        {
            case "click":
                await HandleClickAsync(action, coordinates, cancellationToken).ConfigureAwait(false);
                break;
            case "double_click":
                await HandleDoubleClickAsync(action, coordinates, cancellationToken).ConfigureAwait(false);
                break;
            case "scroll":
                await HandleScrollAsync(action, coordinates, cancellationToken).ConfigureAwait(false);
                break;
            case "move":
                if (_page is not null && coordinates is not null)
                {
                    await _interactionSimulator.MoveMouseAsync(_page, coordinates.Value, cancellationToken).ConfigureAwait(false);
                }
                break;
            case "keypress":
            case "key":
                await HandleKeyPressAsync(action.Keys, cancellationToken).ConfigureAwait(false);
                break;
            case "type":
                if (_page is not null && !string.IsNullOrWhiteSpace(action.Text))
                {
                    await _interactionSimulator.TypeAsync(_page.Keyboard, action.Text, cancellationToken).ConfigureAwait(false);
                }
                break;
            case "wait":
                int delayMs = (int)Math.Clamp(action.DurationMs ?? 1000, 100, 5000);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                break;
            case "screenshot":
                // No action required; next loop iteration will send the screenshot.
                break;
            default:
                _logger.LogDebug("Unhandled computer-use action type '{Type}'.", action.Type);
                break;
        }
    }

    private async Task HandleClickAsync(ComputerUseAction action, (float X, float Y)? coordinates, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        string button = action.Button?.ToLowerInvariant() ?? "left";
        if (button == "back")
        {
            await _page.GoBackAsync().ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (button == "forward")
        {
            await _page.GoForwardAsync().ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (button == "wheel")
        {
            await _interactionSimulator.ScrollAsync(_page, 0, action.ScrollY, cancellationToken).ConfigureAwait(false);
            await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (coordinates is null)
        {
            return;
        }

        MouseButton mappedButton = MapMouseButton(button);
        await _interactionSimulator.ClickAsync(_page, coordinates.Value, mappedButton, cancellationToken).ConfigureAwait(false);
        await WaitForPotentialNavigationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleScrollAsync(ComputerUseAction action, (float X, float Y)? coordinates, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        if (coordinates is not null)
        {
            await _interactionSimulator.MoveMouseAsync(_page, coordinates.Value, cancellationToken).ConfigureAwait(false);
        }

        await _interactionSimulator.ScrollAsync(_page, action.ScrollX, action.ScrollY, cancellationToken).ConfigureAwait(false);
        await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleKeyPressAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (_page is null || keys.Count == 0)
        {
            return;
        }

        string[] translated = keys
            .Select(key => TranslateKey(key))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (translated.Length == 0)
        {
            return;
        }


        await _interactionSimulator.PressKeysAsync(_page.Keyboard, translated, cancellationToken).ConfigureAwait(false);
        bool hasEnter = translated.Any(key => string.Equals(key, "Enter", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "NumpadEnter", StringComparison.OrdinalIgnoreCase));
        if (hasEnter)
        {
            await WaitForPotentialNavigationAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (translated.Any(key => ScrollKeys.Contains(key)))
        {
            await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleDoubleClickAsync(ComputerUseAction action, (float X, float Y)? coordinates, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        string button = action.Button?.ToLowerInvariant() ?? "left";
        if (button is "back" or "forward" or "wheel")
        {
            await HandleClickAsync(action, coordinates, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (coordinates is null)
        {
            return;
        }

        MouseButton mappedButton = MapMouseButton(button);
        await _interactionSimulator.MoveMouseAsync(_page, coordinates.Value, cancellationToken).ConfigureAwait(false);
        await _page.Mouse.DblClickAsync(coordinates.Value.X, coordinates.Value.Y, new MouseDblClickOptions { Button = mappedButton }).ConfigureAwait(false);
        await WaitForPotentialNavigationAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string TranslateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        key = key.Trim();
        if (KeyMapping.TryGetValue(key, out var mapped))
        {
            return mapped;
        }

        return key;
    }

    private void AttachContextEventHandlers()
    {
        if (_context is null)
        {
            return;
        }

        _contextPageHandler ??= HandleContextPage;
        _context.Page -= _contextPageHandler;
        _context.Page += _contextPageHandler;
    }

    private void DetachContextEventHandlers()
    {
        if (_context is null || _contextPageHandler is null)
        {
            return;
        }

        try
        {
            _context.Page -= _contextPageHandler;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detach context page handler.");
        }
    }

    private async void HandleContextPage(object? sender, IPage page)
    {
        if (page is null)
        {
            return;
        }

        if (_page is null || ReferenceEquals(page, _page))
        {
            return;
        }

        string sourceUrl = _page?.Url ?? "(unknown)";
        _logger.LogDebug("Intercepted popup/new tab request from {SourceUrl} -> {PopupUrl}.", sourceUrl, page.Url);

        await RedirectPopupToPrimaryPageAsync(page).ConfigureAwait(false);
    }

    private async Task RedirectPopupToPrimaryPageAsync(IPage popup)
    {
        IPage? primary = _page;
        if (primary is null || popup is null)
        {
            return;
        }

        string? popupUrl = await ResolvePopupUrlAsync(popup).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(popupUrl) || string.Equals(popupUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            RecordTimelineEvent("popup_discarded", new Dictionary<string, object?>
            {
                ["url"] = popupUrl
            });

            await SafeClosePageAsync(popup).ConfigureAwait(false);
            return;
        }

        RecordTimelineEvent("popup_redirecting", new Dictionary<string, object?>
        {
            ["url"] = popupUrl
        });

        bool redirected = false;

        try
        {
            await primary.BringToFrontAsync().ConfigureAwait(false);
            await primary.GotoAsync(popupUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            }).ConfigureAwait(false);
            redirected = true;
            _logger.LogInformation("Redirected popup navigation to primary page ({Url}).", popupUrl);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogDebug(ex, "Popup redirect failed gracefully for {Url}; adopting popup page.", popupUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Popup redirect encountered unexpected error for {Url}; adopting popup page.", popupUrl);
        }

        if (redirected)
        {
            RecordTimelineEvent("popup_redirected", new Dictionary<string, object?>
            {
                ["url"] = popupUrl
            });

            await SafeClosePageAsync(popup).ConfigureAwait(false);
            return;
        }

        _page = popup;
        RecordTimelineEvent("popup_adopted", new Dictionary<string, object?>
        {
            ["url"] = popupUrl
        });

        try
        {
            await popup.BringToFrontAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to bring adopted popup to front ({Url}).", popupUrl);
        }
    }

    private static async Task<string?> ResolvePopupUrlAsync(IPage popup)
    {
        string? popupUrl = null;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                popupUrl = popup.Url;
            }
            catch (PlaywrightException)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(popupUrl) && !string.Equals(popupUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return popupUrl;
            }

            try
            {
                await popup.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 500 }).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (PlaywrightException)
            {
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        try
        {
            return popup.Url;
        }
        catch (PlaywrightException)
        {
            return popupUrl;
        }
    }

    private async Task SafeClosePageAsync(IPage page)
    {
        try
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Failed to close popup page cleanly.");
        }
    }

    private async Task WaitForPotentialNavigationAsync(CancellationToken cancellationToken)
    {
        if (_page is null || _context is null)
        {
            return;
        }

        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 3000 }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);

        var pages = _context.Pages;
        if (pages.Count > 1)
        {
            var newest = pages[^1];
            if (!string.Equals(newest.Url, "about:blank", StringComparison.OrdinalIgnoreCase) && newest != _page)
            {
                _logger.LogDebug("Switching to new browser tab at {Url}.", newest.Url);
                _page = newest;
            }
        }

        await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PreparePageForComputerUseAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
        await DismissConsentAsync(cancellationToken).ConfigureAwait(false);
        await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForVisualStabilityAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        try
        {
            await _page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 2000 }).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (PlaywrightException)
        {
        }

        double? previousHeight = null;
        int stableSamples = 0;
        int attempts = 0;

        while (stableSamples < 3 && attempts < 12)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ViewportSnapshot? snapshot = null;
            try
            {
                snapshot = await _page.EvaluateAsync<ViewportSnapshot?>("""
                    () => {
                        const body = document.body;
                        const scrollHeight = body ? body.scrollHeight : 0;
                        const pendingImages = Array.from(document.images || []).filter(img => !img.complete).length;
                        return { scrollHeight, pendingImages };
                    }
                """).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug(ex, "Visual stability probe failed; retrying.");
            }

            if (snapshot.HasValue)
            {
                double height = snapshot.Value.ScrollHeight;
                bool heightStable = previousHeight is null || Math.Abs(height - previousHeight.Value) < 4;
                bool imagesSettled = snapshot.Value.PendingImages == 0;

                if (heightStable && imagesSettled)
                {
                    stableSamples++;
                }
                else
                {
                    stableSamples = 0;
                }

                previousHeight = height;
            }

            attempts++;
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }

        await Task.Delay(120, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CaptureScreenshotAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            throw new InvalidOperationException("Browser page is not ready for screenshots.");
        }

        try
        {
            byte[] data = await _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = false }).ConfigureAwait(false);
            string base64 = Convert.ToBase64String(data);
            _lastScreenshot = base64;
            // Persist every screenshot with session-aware naming for full traceability
            try
            {
                string root = Path.Combine(Environment.CurrentDirectory, "debug", "computer-use");
                Directory.CreateDirectory(root);
                string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                int seq = Interlocked.Increment(ref _screenshotSequence);
                string session = _currentSessionId ?? "nosession";
                string fileName = $"{session}_{timestamp}_{seq:D4}.png";
                string path = Path.Combine(root, fileName);
                await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
                RecordTimelineEvent("screenshot", new Dictionary<string, object?>
                {
                    ["file"] = Path.GetRelativePath(Environment.CurrentDirectory, path),
                    ["sequence"] = seq
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist per-iteration screenshot.");
            }
            return base64;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Screenshot capture failed, using last successful image if available.");
            if (!string.IsNullOrEmpty(_lastScreenshot))
            {
                return _lastScreenshot;
            }

            throw;
        }
    }

    private async Task<IReadOnlyList<ComputerUseSearchResultItem>> ExtractResultsAsync(string query, IReadOnlyList<string> transcript, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return Array.Empty<ComputerUseSearchResultItem>();
        }

        await ThrowIfCaptchaDetectedAsync(query, cancellationToken).ConfigureAwait(false);

        const string script = @"(() => {
            const serializeResult = (titleNode, linkNode, snippetNode) => {
                if (!titleNode || !linkNode) {
                    return null;
                }
                const title = titleNode.innerText.trim();
                const url = linkNode.href;
                if (!title || !url) {
                    return null;
                }
                const snippet = snippetNode ? snippetNode.innerText.trim() : '';
                return { title, url, snippet };
            };

            const results = [];

            const googleCandidates = document.querySelectorAll('div.g');
            for (const candidate of googleCandidates) {
                const record = serializeResult(
                    candidate.querySelector('h3'),
                    candidate.querySelector('a'),
                    candidate.querySelector('div.VwiC3b, span.aCOpRe, .MUxGbd, .zCubwf')
                );
                if (!record) {
                    continue;
                }
                results.push(record);
                if (results.length >= 5) {
                    break;
                }
            }

            if (results.length === 0) {
                const bingCandidates = document.querySelectorAll('li.b_algo');
                for (const candidate of bingCandidates) {
                    const heading = candidate.querySelector('h2 a');
                    const record = serializeResult(
                        heading,
                        heading,
                        candidate.querySelector('p, div.b_caption p, div.b_snippet')
                    );
                    if (!record) {
                        continue;
                    }
                    results.push(record);
                    if (results.length >= 5) {
                        break;
                    }
                }
            }

            if (results.length === 0) {
                const fallbackHeadings = document.querySelectorAll('a h3');
                for (const titleNode of fallbackHeadings) {
                    const anchor = titleNode.closest('a');
                    if (!anchor) {
                        continue;
                    }
                    const container = anchor.closest('div.g, li.b_algo') || anchor.parentElement;
                    const record = serializeResult(
                        titleNode,
                        anchor,
                        container ? container.querySelector('div.VwiC3b, span.aCOpRe, .MUxGbd, .zCubwf, p, div.b_caption p, div.b_snippet') : null
                    );
                    if (!record) {
                        continue;
                    }
                    results.push(record);
                    if (results.length >= 5) {
                        break;
                    }
                }
            }

            return JSON.stringify(results);
        })()";

        string json = await _page.EvaluateAsync<string>(script).ConfigureAwait(false);
        try
        {
            var normalizedResults = new List<ComputerUseSearchResultItem>();
            var items = JsonSerializer.Deserialize<List<ComputerUseSearchResultItem>>(json, SerializerOptions);

            if (items is not null)
            {
                var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Url))
                    {
                        continue;
                    }

                    string? normalizedUrl = NormalizeNavigableUrl(item.Url);
                    if (string.IsNullOrWhiteSpace(normalizedUrl))
                    {
                        continue;
                    }

                    if (LooksLikeSearchHub(normalizedUrl))
                    {
                        _logger.LogDebug("Skipping search hub URL '{Url}' when extracting results for query '{Query}'.", item.Url, query);
                        continue;
                    }

                    if (!seenUrls.Add(normalizedUrl))
                    {
                        continue;
                    }

                    if (!string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Normalized search result URL from '{Original}' to '{Normalized}' for query '{Query}'.", item.Url, normalizedUrl, query);
                    }

                    normalizedResults.Add(new ComputerUseSearchResultItem(item.Title, normalizedUrl, item.Snippet ?? string.Empty));
                }
            }

            if (normalizedResults.Count == 0)
            {
                string transcriptSummary = transcript.Count == 0 ? "<empty>" : string.Join(" | ", transcript.Take(8));
                string? currentUrl = _page?.Url;
                _logger.LogWarning("Computer-use DOM extraction produced no items for query '{Query}'. CurrentUrl={Url}. Transcript={Transcript}", query, currentUrl, transcriptSummary);
                await PersistDebugArtifactsAsync(query, json, cancellationToken).ConfigureAwait(false);
            }

            return normalizedResults;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse search results DOM.");
            await PersistDebugArtifactsAsync(query, json, cancellationToken).ConfigureAwait(false);
            return Array.Empty<ComputerUseSearchResultItem>();
        }
    }

    private async Task PersistDebugArtifactsAsync(string query, string rawJson, CancellationToken cancellationToken)
    {
        try
        {
            string root = Path.Combine(Environment.CurrentDirectory, "debug", "computer-use");
            Directory.CreateDirectory(root);

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            string safeQuery = SanitizeForFileName(query);

            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                string jsonPath = Path.Combine(root, $"{timestamp}_{safeQuery}_dom.json");
                await File.WriteAllTextAsync(jsonPath, rawJson, cancellationToken).ConfigureAwait(false);
            }

            if (_page is not null)
            {
                string htmlPath = Path.Combine(root, $"{timestamp}_{safeQuery}_page.html");
                string pageHtml = await _page.ContentAsync().ConfigureAwait(false);
                await File.WriteAllTextAsync(htmlPath, pageHtml, cancellationToken).ConfigureAwait(false);
            }

            string screenshotBase64 = _lastScreenshot ?? await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
            string screenshotPath = Path.Combine(root, $"{timestamp}_{safeQuery}.png");
            byte[] bytes = Convert.FromBase64String(screenshotBase64);
            await File.WriteAllBytesAsync(screenshotPath, bytes, cancellationToken).ConfigureAwait(false);

            RecordTimelineEvent("debug_artifacts_persisted", new Dictionary<string, object?>
            {
                ["domFile"] = $"{timestamp}_{safeQuery}_dom.json",
                ["htmlFile"] = $"{timestamp}_{safeQuery}_page.html",
                ["pngFile"] = $"{timestamp}_{safeQuery}.png"
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist computer-use debug artifacts for query '{Query}'.", query);
        }
    }

    private async Task EnsureSearchResultsAsync(string query, CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        await ThrowIfCaptchaDetectedAsync(query, cancellationToken).ConfigureAwait(false);

        bool hasResults = false;
        try
        {
            hasResults = await _page.EvaluateAsync<bool>("() => !!document.querySelector('li.b_algo, div.g')").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Failed to evaluate current SERP state before fallback navigation.");
        }

        if (hasResults)
        {
            return;
        }

    string targetUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}&hl=en";
    _logger.LogInformation("No SERP content detected; navigating directly to Google results '{Url}'.", targetUrl);

        try
        {
            await _page.GotoAsync(targetUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogWarning(ex, "Direct navigation to Bing results failed for query '{Query}'.", query);
        }

        await DismissConsentAsync(cancellationToken).ConfigureAwait(false);
        await ThrowIfCaptchaDetectedAsync(query, cancellationToken).ConfigureAwait(false);
        await WaitForSerpContentAsync(cancellationToken).ConfigureAwait(false);
        await ThrowIfCaptchaDetectedAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task DismissConsentAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        foreach (IFrame frame in _page.Frames)
        {
            if (await TryDismissConsentSelectorsAsync(frame).ConfigureAwait(false))
            {
                _logger.LogDebug("Dismissed consent dialog using selector in frame '{FrameInfo}'.", DescribeFrame(frame));
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        foreach (IFrame frame in _page.Frames)
        {
            if (await TryDismissConsentByRoleAsync(frame).ConfigureAwait(false))
            {
                _logger.LogDebug("Dismissed consent dialog using role lookup in frame '{FrameInfo}'.", DescribeFrame(frame));
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        foreach (IFrame frame in _page.Frames)
        {
            if (await TryDismissConsentViaScriptAsync(frame).ConfigureAwait(false))
            {
                _logger.LogDebug("Dismissed consent dialog using script in frame '{FrameInfo}'.", DescribeFrame(frame));
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        try
        {
            bool removed = await _page.EvaluateAsync<bool>("() => { const overlays = ['#bnp_container', '#bnp_dialog', '.bnp_container', '.bnp_dialog']; let removed = false; for (const selector of overlays) { document.querySelectorAll(selector).forEach(el => { el.remove(); removed = true; }); } return removed; }").ConfigureAwait(false);
            if (removed)
            {
                _logger.LogDebug("Removed consent overlay containers from main frame.");
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Consent removal script encountered an error.");
        }
    }

    private async Task<bool> TryDismissConsentSelectorsAsync(IFrame frame)
    {
        foreach (string selector in ConsentButtonSelectors)
        {
            try
            {
                ILocator locator = frame.Locator(selector);
                if (await locator.CountAsync().ConfigureAwait(false) == 0)
                {
                    continue;
                }

                await locator.First.ClickAsync(new LocatorClickOptions { Timeout = 800, Force = true }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogTrace(ex, "Consent selector '{Selector}' not clickable in frame '{FrameInfo}'.", selector, DescribeFrame(frame));
            }
        }

        return false;
    }

    private async Task<bool> TryDismissConsentByRoleAsync(IFrame frame)
    {
        foreach (Regex pattern in ConsentButtonNamePatterns)
        {
            try
            {
                var options = new FrameGetByRoleOptions { NameRegex = pattern };
                ILocator locator = frame.GetByRole(AriaRole.Button, options);
                if (await locator.CountAsync().ConfigureAwait(false) == 0)
                {
                    continue;
                }

                await locator.First.ClickAsync(new LocatorClickOptions { Timeout = 800, Force = true }).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogTrace(ex, "Role-based consent dismissal failed for pattern '{Pattern}' in frame '{FrameInfo}'.", pattern, DescribeFrame(frame));
            }
        }

        return false;
    }

    private async Task<bool> TryDismissConsentViaScriptAsync(IFrame frame)
    {
        try
        {
            return await frame.EvaluateAsync<bool>(ConsentDismissScript).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            _logger.LogTrace(ex, "Consent dismissal script failed in frame '{FrameInfo}'.", DescribeFrame(frame));
        }

        return false;
    }

    private static string DescribeFrame(IFrame frame)
    {
        try
        {
            string name = frame.Name;
            string url = frame.Url;
            if (string.IsNullOrWhiteSpace(name))
            {
                return url;
            }

            return string.IsNullOrWhiteSpace(url) ? name : $"{name} ({url})";
        }
        catch
        {
            return "<unknown frame>";
        }
    }

    private async Task WaitForSerpContentAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

    float serpTimeout = 4000;
        if (ShouldApplyTimeout(_timeouts.DefaultActionTimeout))
        {
            serpTimeout = ClampTimeout(_timeouts.DefaultActionTimeout, 1000, 120000);
        }
        else if (ShouldApplyTimeout(_timeouts.SearchOperationTimeout))
        {
            serpTimeout = ClampTimeout(_timeouts.SearchOperationTimeout, 1000, 300000);
        }

        try
        {
            await _page.WaitForSelectorAsync(
                "li.b_algo, div.g",
                new PageWaitForSelectorOptions { Timeout = serpTimeout }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogDebug(ex, "SERP content did not appear within the expected timeframe.");
        }

        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
    }

    private async Task ThrowIfCaptchaDetectedAsync(string query, CancellationToken cancellationToken)
    {
        if (!await IsCaptchaDetectedAsync().ConfigureAwait(false))
        {
            return;
        }

    _logger.LogWarning("Detected CAPTCHA / challenge page during computer-use search for query '{Query}'.", query);
        // capture current state so saved artifacts reflect the actual challenge screen
        try
        {
            await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture screenshot while handling CAPTCHA detection.");
        }
        RecordTimelineEvent("captcha_detected", new Dictionary<string, object?>
        {
            ["query"] = query
        });
        await PersistDebugArtifactsAsync(query, "{\"captcha\":true}", cancellationToken).ConfigureAwait(false);
    throw new ComputerUseSearchBlockedException("Encountered a CAPTCHA / bot challenge from the search engine while using Playwright automation.");
    }

    private async Task<bool> IsCaptchaDetectedAsync()
    {
        if (_page is null)
        {
            return false;
        }

        try
        {
            const string script = @"() => {
                const selectors = ['.captcha', '#cf-chl-widget', '#turnstile-widget', 'iframe[src*=""challenges.cloudflare.com""]', 'form[action*=""/challenge/""]'];
                if (selectors.some(selector => document.querySelector(selector))) {
                    return true;
                }

                const title = (document.title || '').toLowerCase();
                if (title.includes('just a moment') || title.includes('captcha')) {
                    return true;
                }

                const bodyText = ((document.body && document.body.innerText) || '').toLowerCase();
                if (bodyText.includes('please solve the challenge')) {
                    return true;
                }

                return false;
            }";

            return await _page.EvaluateAsync<bool>(script).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Captcha detection script failed.");
            return false;
        }
    }

    private static string SanitizeForFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "query";
        }

        Span<char> buffer = stackalloc char[value.Length];
        int index = 0;
        foreach (char ch in value)
        {
            buffer[index++] = Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0 ? '_' : ch;
        }

        return new string(buffer[..index]);
    }

    private static bool ShouldApplyTimeout(TimeSpan value)
        => value > TimeSpan.Zero && value != Timeout.InfiniteTimeSpan;

    private static float ClampTimeout(TimeSpan value, double minimum, double maximum)
    {
        double milliseconds = value.TotalMilliseconds;
        double clamped = Math.Clamp(milliseconds, minimum, maximum);
        return (float)clamped;
    }

    private (float X, float Y)? ClampCoordinates(double? x, double? y)
    {
        if (x is null || y is null)
        {
            return null;
        }

        float clampedX = (float)Math.Clamp(x.Value, 0, _viewportWidth);
        float clampedY = (float)Math.Clamp(y.Value, 0, _viewportHeight);
        return (clampedX, clampedY);
    }

    private static MouseButton MapMouseButton(string button)
    {
        return button switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
        };
    }

    private static string? TruncateForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= 80 ? value : value.Substring(0, 77) + "...";
    }

    private void InitializeTimeline(string query)
    {
        _timelineEntries = new List<TimelineEntry>
        {
            new TimelineEntry(DateTimeOffset.UtcNow, "session_start", new Dictionary<string, object?>
            {
                ["query"] = query,
                ["sessionId"] = _currentSessionId,
                ["viewport"] = new Dictionary<string, object?>
                {
                    ["width"] = _viewportWidth,
                    ["height"] = _viewportHeight
                },
                ["proxy"] = _activeProxy?.Endpoint.Uri,
                ["locale"] = _activeProxy?.Endpoint.PreferredLocale ?? _environmentOptions.PreferredLocale,
                ["timeZone"] = _browserSession?.TimeZoneId ?? "UTC",
                ["requestedTimeZone"] = _activeProxy?.Endpoint.PreferredTimeZoneId ?? _environmentOptions.PreferredTimeZoneId ?? TimeZoneInfo.Local.Id
            })
        };
        string root = Path.Combine(Environment.CurrentDirectory, "debug", "computer-use");
        Directory.CreateDirectory(root);
        _timelinePath = _currentSessionId is null
            ? null
            : Path.Combine(root, $"{_currentSessionId}_timeline.json");
    }

    private void RecordTimelineEvent(string eventName, Dictionary<string, object?>? data = null)
    {
        if (_timelineEntries is null)
        {
            return;
        }

        _timelineEntries.Add(new TimelineEntry(DateTimeOffset.UtcNow, eventName, data ?? new Dictionary<string, object?>()));
    }

    private async Task PersistTimelineAsync(string query, CancellationToken cancellationToken)
    {
        if (_timelineEntries is null || _timelineEntries.Count == 0 || string.IsNullOrWhiteSpace(_timelinePath))
        {
            return;
        }

        try
        {
            var serializerOptions = new JsonSerializerOptions(SerializerOptions)
            {
                WriteIndented = true
            };

            var payload = JsonSerializer.Serialize(_timelineEntries, serializerOptions);
            await File.WriteAllTextAsync(_timelinePath, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write timeline for query '{Query}'.", query);
        }
        finally
        {
            _timelineEntries = null;
            _timelinePath = null;
        }
    }

    private sealed record TimelineEntry(DateTimeOffset TimestampUtc, string Event, Dictionary<string, object?> Data);

    private async Task ResetBrowserSessionAsync()
    {
        DetachContextEventHandlers();

        if (_browserSession is not null)
        {
            try
            {
                await _browserSession.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Failed to dispose browser session during timeout recovery.");
            }
        }
        else
        {
            if (_page is not null)
            {
                try
                {
                    await _page.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Failed to close Playwright page during timeout recovery.");
                }
            }

            if (_context is not null)
            {
                try
                {
                    await _context.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Failed to close Playwright context during timeout recovery.");
                }
            }

            if (_browser is not null)
            {
                try
                {
                    await _browser.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "Failed to close Playwright browser during timeout recovery.");
                }
            }

            if (_playwright is not null)
            {
                try
                {
                    _playwright.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose Playwright during timeout recovery.");
                }
            }
        }

        _browserSession = null;
        _page = null;
        _context = null;
        _browser = null;
        _playwright = null;
        _activeProxy = null;
    _interactionSimulator.Reset();
        _initialized = false;
        _lastScreenshot = null;
        _currentSessionId = null;
        _timelineEntries = null;
        _timelinePath = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetBrowserSessionAsync().ConfigureAwait(false);

        _operationLock.Dispose();
        _initializationLock.Dispose();
    }

    private sealed record ComputerUseSafetyCheck(string Id, string Code, string Message);

    private sealed record ComputerUseAction(
        string Type,
        double? X,
        double? Y,
        string? Button,
        double ScrollX,
        double ScrollY,
        IReadOnlyList<string> Keys,
        string? Text,
        double? DurationMs);

    private sealed record ComputerCall(string CallId, ComputerUseAction Action, IReadOnlyList<ComputerUseSafetyCheck> PendingSafetyChecks);

    private sealed record ComputerUseResponseState(
        string? ResponseId,
        ComputerCall? Call,
        bool Completed,
        string? SummaryFunctionCallId)
    {
        public ExplorationPayload? SummaryPayload { get; init; }

        public string? SummaryRawJson { get; init; }

        public string? SummaryResponseId { get; init; }

        public IReadOnlyList<string>? FlaggedFunctionCallIds { get; init; }
    }

    private readonly record struct ViewportSnapshot(double ScrollHeight, int PendingImages);

    private async Task<ComputerUseExplorationResult> ExecuteExplorationCoreAsync(string url, string? objective, CancellationToken cancellationToken)
    {
        try
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            _screenshotSequence = 0;
            InitializeTimeline($"explore::{url}");
            if (_page is null)
            {
                throw new InvalidOperationException("Computer-use browser page was not initialized.");
            }

            int requiredFlaggedResourceCount = ResolveRequiredFlaggedResourceCount(objective);
            string flagRequirementDirective = BuildFlagRequirementDirective(requiredFlaggedResourceCount);
            string explorationInstructionSet = string.IsNullOrWhiteSpace(flagRequirementDirective)
                ? ExplorationInstructions
                : $"{ExplorationInstructions}\n\n{flagRequirementDirective}";
            string flagReminderInstructionsSuffix = BuildFlagReminderInstructionsSuffix(requiredFlaggedResourceCount);

            RecordTimelineEvent("exploration_start", new Dictionary<string, object?>
            {
                ["url"] = url,
                ["objective"] = objective,
                ["requiredFlaggedResources"] = requiredFlaggedResourceCount
            });

            await _page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 20000
            }).ConfigureAwait(false);
            await PreparePageForComputerUseAsync(cancellationToken).ConfigureAwait(false);
            await ThrowIfCaptchaDetectedAsync(url, cancellationToken).ConfigureAwait(false);
            await _page.BringToFrontAsync().ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            var transcript = new List<string>();
            var acknowledgedFlaggedCallIds = new HashSet<string>(StringComparer.Ordinal);
            ComputerUseResponseState response = await SendInitialExplorationRequestAsync(url, objective, transcript, explorationInstructionSet, flagRequirementDirective, cancellationToken).ConfigureAwait(false);
            int iteration = 0;
            int continuationAttempts = 0;
            int flagReminderAttempts = 0;
            bool summaryRequested = false;
            string? lastResponseId = response.ResponseId;
            ExplorationPayload? summaryPayload = response.SummaryPayload;
            string? summaryRawJson = response.SummaryRawJson;
            string? summaryCallId = response.SummaryFunctionCallId;
            string? summaryResponseId = response.SummaryResponseId;
            string? acknowledgedSummaryCallId = null;

            async Task ProcessFlaggedFunctionsAsync()
            {
                (response, lastResponseId) = await HandleFlaggedFunctionCallsAsync(response, transcript, acknowledgedFlaggedCallIds, lastResponseId, cancellationToken).ConfigureAwait(false);
            }

            void UpdateSummary(ComputerUseResponseState state)
            {
                if (state.SummaryPayload is not null)
                {
                    summaryPayload = state.SummaryPayload;
                    summaryRawJson = state.SummaryRawJson;
                }

                if (!string.IsNullOrWhiteSpace(state.SummaryFunctionCallId))
                {
                    summaryCallId = state.SummaryFunctionCallId;
                }

                if (!string.IsNullOrWhiteSpace(state.SummaryResponseId))
                {
                    summaryResponseId = state.SummaryResponseId;
                }
                else if (string.IsNullOrWhiteSpace(state.SummaryFunctionCallId))
                {
                    summaryResponseId = null;
                }
            }

            async Task TryAcknowledgeSummaryAsync()
            {
                if (!_supportsSummaryFunction)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(summaryCallId) ||
                    string.Equals(summaryCallId, acknowledgedSummaryCallId, StringComparison.Ordinal))
                {
                    return;
                }

                ComputerUseResponseState ackSource = response;
                string? ackResponseId = !string.IsNullOrWhiteSpace(ackSource.ResponseId)
                    ? ackSource.ResponseId
                    : summaryResponseId;

                if (string.IsNullOrWhiteSpace(ackResponseId) && !string.IsNullOrWhiteSpace(lastResponseId))
                {
                    ackResponseId = lastResponseId;
                }

                if (string.IsNullOrWhiteSpace(ackResponseId))
                {
                    return;
                }

                string callId = summaryCallId!;
                string? rawJson = summaryRawJson;

                ackSource = ackSource with { ResponseId = ackResponseId };

                ackSource = await SendSummaryFunctionOutputAsync(ackSource, transcript, callId, rawJson, cancellationToken).ConfigureAwait(false);
                response = ackSource;

                acknowledgedSummaryCallId = callId;
                summaryResponseId = null;
                UpdateSummary(response);
                await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(response.ResponseId))
                {
                    lastResponseId = response.ResponseId;
                }
                else if (!string.IsNullOrWhiteSpace(lastResponseId))
                {
                    response = response with { ResponseId = lastResponseId };
                }
            }

            int GetFlaggedResourceCount()
            {
                return GetDistinctFlaggedResourceCount(summaryPayload);
            }

            bool HasRequiredFlaggedResources()
            {
                return GetFlaggedResourceCount() >= requiredFlaggedResourceCount;
            }


            await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
            UpdateSummary(response);
            await TryAcknowledgeSummaryAsync().ConfigureAwait(false);

            while (iteration < MaxIterations)
            {
                if (response.Call is not null)
                {
                    iteration++;
                    await HandleComputerCallAsync(response.Call, cancellationToken).ConfigureAwait(false);
                    response = await SendFollowupRequestAsync(response, transcript, cancellationToken, explorationInstructionSet).ConfigureAwait(false);
                    await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
                    UpdateSummary(response);
                    await TryAcknowledgeSummaryAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(response.ResponseId))
                    {
                        lastResponseId = response.ResponseId;
                    }
                    else if (!string.IsNullOrWhiteSpace(lastResponseId))
                    {
                        response = response with { ResponseId = lastResponseId };
                    }
                    continue;
                }

                int flaggedResourceCount = GetFlaggedResourceCount();
                bool hasRequiredFlaggedResources = flaggedResourceCount >= requiredFlaggedResourceCount;
                bool shouldRequestMore = ShouldRequestExplorationContinuation(iteration, transcript, hasRequiredFlaggedResources);
                string? effectiveResponseId = !string.IsNullOrWhiteSpace(response.ResponseId) ? response.ResponseId : lastResponseId;
                bool canRequestActions = !string.IsNullOrWhiteSpace(effectiveResponseId) &&
                    (hasRequiredFlaggedResources
                        ? continuationAttempts < MaxContinuationAttempts
                        : flagReminderAttempts < MaxFlagReminderAttempts);

                if (shouldRequestMore && canRequestActions)
                {
                    if (hasRequiredFlaggedResources)
                    {
                        continuationAttempts++;
                    }
                    else
                    {
                        flagReminderAttempts++;
                    }

                    string attemptType = hasRequiredFlaggedResources ? "continuation" : "flag-reminder";
                    string continuationPrompt = hasRequiredFlaggedResources
                        ? ExplorationContinuationPrompt
                        : BuildFlagReminderPrompt(requiredFlaggedResourceCount, flaggedResourceCount);
                    string continuationInstructions = hasRequiredFlaggedResources
                        ? explorationInstructionSet
                        : $"{explorationInstructionSet}\n\n{flagReminderInstructionsSuffix}";

                    _logger.LogInformation(
                        "Forcing additional computer-use exploration (mode={Mode}, attempt {Attempt}, iteration {Iteration}, segments={Segments}, flagged={Flagged}).",
                        attemptType,
                        hasRequiredFlaggedResources ? continuationAttempts : flagReminderAttempts,
                        iteration,
                        transcript.Count,
                        flaggedResourceCount);

                    var continuationState = !string.IsNullOrWhiteSpace(response.ResponseId)
                        ? response
                        : response with { ResponseId = effectiveResponseId };

                    response = await RequestContinuationAsync(
                            continuationState,
                            transcript,
                            continuationPrompt,
                            continuationInstructions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
                    UpdateSummary(response);
                    await TryAcknowledgeSummaryAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(response.ResponseId))
                    {
                        lastResponseId = response.ResponseId;
                    }
                    else if (!string.IsNullOrWhiteSpace(effectiveResponseId))
                    {
                        response = response with { ResponseId = effectiveResponseId };
                    }
                    continue;
                }

                bool needsSummary = !summaryRequested && ShouldRequestExplorationSummary(transcript, hasRequiredFlaggedResources);
                bool canRequestSummary = !string.IsNullOrWhiteSpace(effectiveResponseId);

                if (needsSummary && canRequestSummary)
                {
                    summaryRequested = true;
                    _logger.LogInformation(
                        "Requesting computer-use exploration summary (iteration {Iteration}, segments={Segments}).",
                        iteration,
                        transcript.Count);

                    var summaryState = !string.IsNullOrWhiteSpace(response.ResponseId)
                        ? response
                        : response with { ResponseId = effectiveResponseId };

                    response = await RequestContinuationAsync(
                            summaryState,
                            transcript,
                            ExplorationSummaryPrompt,
                            ExplorationSummaryInstructions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
                    UpdateSummary(response);
                    await TryAcknowledgeSummaryAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(response.ResponseId))
                    {
                        lastResponseId = response.ResponseId;
                    }
                    else if (!string.IsNullOrWhiteSpace(effectiveResponseId))
                    {
                        response = response with { ResponseId = effectiveResponseId };
                    }
                    continue;
                }

                break;
            }

            if (iteration >= MaxIterations && response.Call is not null)
            {
                _logger.LogWarning("Computer-use exploration for url '{Url}' reached iteration limit before completion.", url);
            }

            if (!summaryRequested && response.Call is null && HasRequiredFlaggedResources() && !string.IsNullOrWhiteSpace(lastResponseId))
            {
                try
                {
                    _logger.LogInformation(
                        "Requesting computer-use exploration summary after action loop (segments={Segments}).",
                        transcript.Count);

                    var summaryState = response with { ResponseId = lastResponseId };
                    ComputerUseResponseState summaryResponse = await RequestContinuationAsync(
                            summaryState,
                            transcript,
                            ExplorationSummaryPrompt,
                            ExplorationSummaryInstructions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    response = summaryResponse;
                    await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
                    UpdateSummary(summaryResponse);
                    await TryAcknowledgeSummaryAsync().ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(summaryResponse.ResponseId))
                    {
                        lastResponseId = summaryResponse.ResponseId;
                    }

                    if (summaryResponse.Call is not null && iteration < MaxIterations)
                    {
                        response = summaryResponse;
                        summaryRequested = true;

                        while (response.Call is not null && iteration < MaxIterations)
                        {
                            iteration++;
                            await HandleComputerCallAsync(response.Call, cancellationToken).ConfigureAwait(false);
                            response = await SendFollowupRequestAsync(response, transcript, cancellationToken, explorationInstructionSet).ConfigureAwait(false);
                            await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
                            UpdateSummary(response);
                            await TryAcknowledgeSummaryAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(response.ResponseId))
                            {
                                lastResponseId = response.ResponseId;
                            }
                            else if (!string.IsNullOrWhiteSpace(lastResponseId))
                            {
                                response = response with { ResponseId = lastResponseId };
                            }
                        }
                    }
                    else
                    {
                        summaryRequested = true;
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Summary request failed; continuing with collected transcript only.");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Summary request failed; continuing with collected transcript only.");
                }
            }

            await ProcessFlaggedFunctionsAsync().ConfigureAwait(false);
            int finalFlaggedCount = GetFlaggedResourceCount();
            bool finalHasFlaggedResources = finalFlaggedCount > 0;
            string? finalUrl = null;
            string? pageTitle = null;
            try
            {
                finalUrl = _page.Url;
                pageTitle = await _page.TitleAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Failed to capture final page metadata for exploration '{Url}'.", url);
            }

            bool attemptedAutoFlag = false;
            if (!finalHasFlaggedResources)
            {
                attemptedAutoFlag = true;
                if (TryAutoFlagFinalPage(finalUrl, pageTitle, objective, transcript))
                {
                    finalHasFlaggedResources = true;
                    finalFlaggedCount = GetFlaggedResourceCount();
                }
            }

            ExplorationStructuredOutput structured = ParseExplorationStructuredOutput(transcript, summaryPayload, summaryRawJson);
            string summary = !string.IsNullOrWhiteSpace(structured.Summary)
                ? structured.Summary!
                : ExtractExplorationSummary(transcript);

            if (string.IsNullOrWhiteSpace(summary) || summary.Length < 80)
            {
                string? fallbackSummary = await CapturePageSynopsisAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fallbackSummary))
                {
                    summary = fallbackSummary;
                }
            }

            IReadOnlyList<string> findings = ExtractExplorationFindings(structured.Findings, summary, transcript);

            if (findings.Count == 0 && !string.IsNullOrWhiteSpace(summary))
            {
                findings = ExtractExplorationFindings(Array.Empty<string>(), summary, transcript);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                if (!string.IsNullOrWhiteSpace(structured.RawJson))
                {
                    _logger.LogInformation("Structured exploration output JSON: {Structured}", structured.RawJson);
                }
                else
                {
                    _logger.LogInformation("Structured exploration output JSON unavailable; falling back to transcript findings.");
                }
            }

            int structuredFlaggedCount = structured.FlaggedResources.Count;

            if (structuredFlaggedCount == 0)
            {
                RecordTimelineEvent("flagging_failure", new Dictionary<string, object?>
                {
                    ["reason"] = "no_resources_flagged",
                    ["iterations"] = iteration,
                    ["continuationAttempts"] = continuationAttempts,
                    ["flagReminderAttempts"] = flagReminderAttempts,
                    ["autoFlagAttempted"] = attemptedAutoFlag,
                    ["finalUrl"] = finalUrl,
                    ["required"] = requiredFlaggedResourceCount,
                    ["actual"] = structuredFlaggedCount
                });

                throw new ComputerUseFlaggingException("Exploration completed without any flagged resources.");
            }
            else if (structuredFlaggedCount < requiredFlaggedResourceCount)
            {
                RecordTimelineEvent("flagging_shortfall", new Dictionary<string, object?>
                {
                    ["reason"] = "below_requirement",
                    ["iterations"] = iteration,
                    ["continuationAttempts"] = continuationAttempts,
                    ["flagReminderAttempts"] = flagReminderAttempts,
                    ["autoFlagAttempted"] = attemptedAutoFlag,
                    ["finalUrl"] = finalUrl,
                    ["required"] = requiredFlaggedResourceCount,
                    ["actual"] = structuredFlaggedCount
                });
            }

            if (structured.FlaggedResources.Count > 0)
            {
                RecordTimelineEvent("flagged_resources", new Dictionary<string, object?>
                {
                    ["count"] = structured.FlaggedResources.Count,
                    ["required"] = requiredFlaggedResourceCount,
                    ["items"] = structured.FlaggedResources.Select(resource => new Dictionary<string, object?>
                    {
                        ["type"] = resource.Type.ToString(),
                        ["title"] = resource.Title,
                        ["url"] = resource.Url,
                        ["mimeType"] = resource.MimeType,
                        ["notes"] = resource.Notes
                    }).ToArray()
                });
            }

            if (findings.Count > 0)
            {
                RecordTimelineEvent("exploration_findings", new Dictionary<string, object?>
                {
                    ["count"] = findings.Count,
                    ["items"] = findings.ToArray()
                });
            }

            RecordTimelineEvent("exploration_complete", new Dictionary<string, object?>
            {
                ["requestedUrl"] = url,
                ["finalUrl"] = finalUrl,
                ["summary"] = summary,
                ["findings"] = findings.ToArray()
            });

            return new ComputerUseExplorationResult(url, finalUrl, pageTitle, summary, structured.RawJson, findings, transcript.ToArray(), structured.FlaggedResources);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ComputerUseSearchBlockedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Computer-use exploration failed for url '{Url}'.", url);
            throw;
        }
    }

    private async Task<ComputerUseResponseState> SendInitialExplorationRequestAsync(string targetUrl, string? objective, List<string> transcript, string explorationInstructions, string flagRequirementDirective, CancellationToken cancellationToken)
    {
        await PreparePageForComputerUseAsync(cancellationToken).ConfigureAwait(false);
        string screenshot = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        string focusSubject = string.IsNullOrWhiteSpace(objective) ? "the topic" : $"\"{objective}\"";
        string userInstruction =
            $"Review the currently open page and gather concrete insights related to {focusSubject}. " +
            "If the page is a navigation hub or search form, use it to reach the relevant content before summarizing. " +
            "Scroll as needed, open helpful links, and capture evidence. " +
            $"The requested page is {targetUrl}. When you are ready to conclude, call the submit_summary function exactly once with a concise summary, at least three findings, and any important resources or outbound links.";

        if (!string.IsNullOrWhiteSpace(flagRequirementDirective))
        {
            userInstruction += $" {flagRequirementDirective}";
        }

        var payload = new JsonObject
        {
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "input_text",
                            ["text"] = userInstruction
                        },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:image/png;base64,{screenshot}"
                        }
                    }
                }
            },
                ["instructions"] = explorationInstructions,
            ["tools"] = BuildToolsArray(),
            ["reasoning"] = new JsonObject { ["generate_summary"] = "concise" },
            ["temperature"] = 0.2,
            ["top_p"] = 0.8,
            ["truncation"] = "auto"
        };
        payload["model"] = _deployment;

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private static bool ShouldRequestExplorationContinuation(int iteration, IReadOnlyList<string> transcript, bool hasRequiredFlaggedResources)
    {
        if (!hasRequiredFlaggedResources)
        {
            return true;
        }

        if (iteration < MinExplorationIterations)
        {
            return true;
        }

        int informativeSegments = 0;
        int nonEmptySegments = 0;

        foreach (string segment in transcript)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            nonEmptySegments++;
            if (segment.Length >= 80)
            {
                informativeSegments++;
            }
        }

        if (informativeSegments == 0)
        {
            return true;
        }

        return nonEmptySegments < 3;
    }

    private static bool ShouldRequestExplorationSummary(IReadOnlyList<string> transcript, bool hasRequiredFlaggedResources)
    {
        if (!hasRequiredFlaggedResources)
        {
            return false;
        }

        if (transcript.Count == 0)
        {
            return true;
        }

        for (int index = transcript.Count - 1; index >= 0; index--)
        {
            string segment = transcript[index];
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (segment.Length >= 120)
            {
                return false;
            }

            break;
        }

        int nonEmptySegments = transcript.Count(static segment => !string.IsNullOrWhiteSpace(segment));
        return nonEmptySegments < 3;
    }

    private ExplorationStructuredOutput ParseExplorationStructuredOutput(IReadOnlyList<string> transcript, ExplorationPayload? summaryPayload, string? summaryRawJson)
    {
        FlaggedResource[] flaggedFromToolCalls = _flaggedResources.Count == 0
            ? Array.Empty<FlaggedResource>()
            : _flaggedResources.ToArray();

        if (summaryPayload is not null)
        {
            return ConvertPayloadToStructuredOutput(summaryPayload, summaryRawJson, flaggedFromToolCalls);
        }

        if (transcript.Count == 0)
        {
            return new ExplorationStructuredOutput(null, Array.Empty<string>(), flaggedFromToolCalls, null);
        }

        for (int index = transcript.Count - 1; index >= 0; index--)
        {
            string entry = transcript[index];
            if (!TryParseExplorationPayload(entry, out var payload, out string? rawJson))
            {
                continue;
            }

            return ConvertPayloadToStructuredOutput(payload, rawJson, flaggedFromToolCalls);
        }

        return new ExplorationStructuredOutput(null, Array.Empty<string>(), flaggedFromToolCalls, null);
    }

    private static ExplorationStructuredOutput ConvertPayloadToStructuredOutput(ExplorationPayload payload, string? rawJson, IReadOnlyCollection<FlaggedResource> flaggedFromToolCalls)
    {
        IReadOnlyList<string> findings = payload.Findings is { Count: > 0 }
            ? payload.Findings
                .Select(static item => item?.Trim())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Length > 320 ? item[..320] + "…" : item)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

        var flaggedLookup = new Dictionary<string, FlaggedResource>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void AddOrMerge(FlaggedResource candidate)
        {
            if (flaggedLookup.TryGetValue(candidate.Url, out var existing))
            {
                flaggedLookup[candidate.Url] = MergeFlaggedResource(existing, candidate);
            }
            else
            {
                flaggedLookup[candidate.Url] = candidate;
                order.Add(candidate.Url);
            }
        }

        if (payload.Flagged is not null)
        {
            foreach (FlaggedResource? resource in payload.Flagged.Select(TryConvertResource))
            {
                if (resource is not null)
                {
                    AddOrMerge(resource);
                }
            }
        }

        if (flaggedFromToolCalls.Count > 0)
        {
            foreach (FlaggedResource resource in flaggedFromToolCalls)
            {
                AddOrMerge(resource);
            }
        }

        List<FlaggedResource> flagged = order.Count == 0
            ? new List<FlaggedResource>()
            : order.Select(url => flaggedLookup[url]).ToList();

        return new ExplorationStructuredOutput(payload.Summary, findings, flagged, rawJson);
    }

    private bool TryParseExplorationPayload(string entry, out ExplorationPayload payload, out string? rawJson)
    {
        payload = new ExplorationPayload();
        rawJson = null;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        string trimmed = entry.Trim();
        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        string candidate = trimmed[start..(end + 1)];
        try
        {
            ExplorationPayload? parsed = JsonSerializer.Deserialize<ExplorationPayload>(candidate, SerializerOptions);
            if (parsed is null)
            {
                return false;
            }

            bool hasSummary = !string.IsNullOrWhiteSpace(parsed.Summary);
            bool hasFindings = parsed.Findings is { Count: > 0 };
            bool hasFlagged = parsed.Flagged is { Count: > 0 };

            if (!hasSummary && !hasFindings && !hasFlagged)
            {
                return false;
            }

            payload = parsed;
            rawJson = candidate;
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to parse structured exploration payload. Raw={Payload}", candidate);
            return false;
        }
    }

    private static FlaggedResource? TryConvertResource(ExplorationPayloadResource resource)
    {
        if (resource is null)
        {
            return null;
        }

        string? url = resource.Url?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string? normalizedUrl = NormalizeNavigableUrl(url);
        if (string.IsNullOrWhiteSpace(normalizedUrl) || LooksLikeSearchHub(normalizedUrl))
        {
            return null;
        }

        url = normalizedUrl;

        string title = string.IsNullOrWhiteSpace(resource.Title) ? url : resource.Title!.Trim();
        string? mime = string.IsNullOrWhiteSpace(resource.MimeType) ? null : resource.MimeType!.Trim();
        string? notes = string.IsNullOrWhiteSpace(resource.Notes) ? null : resource.Notes!.Trim();

        FlaggedResourceType type = ParseResourceType(resource.Type);

        if (type == FlaggedResourceType.Page && LooksLikePdf(url, mime))
        {
            type = FlaggedResourceType.File;
        }

        if (type is FlaggedResourceType.File or FlaggedResourceType.Download && mime is null && LooksLikePdf(url, mime))
        {
            mime = "application/pdf";
        }

        return new FlaggedResource(type, title, url, mime, notes);
    }

    private static FlaggedResource MergeFlaggedResource(FlaggedResource existing, FlaggedResource incoming)
    {
        FlaggedResourceType type = existing.Type;
        if (type == FlaggedResourceType.Page && incoming.Type != FlaggedResourceType.Page)
        {
            type = incoming.Type;
        }

        string title = SelectPreferredTitle(existing.Title, incoming.Title, existing.Url);
        string? mimeType = SelectPreferredOptional(existing.MimeType, incoming.MimeType);
        string? notes = SelectPreferredOptional(existing.Notes, incoming.Notes);

        return existing with
        {
            Type = type,
            Title = title,
            MimeType = mimeType,
            Notes = notes
        };
    }

    private static string SelectPreferredTitle(string currentTitle, string incomingTitle, string url)
    {
        string normalizedCurrent = string.IsNullOrWhiteSpace(currentTitle) ? url : currentTitle.Trim();
        string normalizedIncoming = string.IsNullOrWhiteSpace(incomingTitle) ? url : incomingTitle.Trim();

        bool currentIsPlaceholder = string.Equals(normalizedCurrent, url, StringComparison.OrdinalIgnoreCase);
        bool incomingIsPlaceholder = string.Equals(normalizedIncoming, url, StringComparison.OrdinalIgnoreCase);

        if (currentIsPlaceholder && !incomingIsPlaceholder)
        {
            return normalizedIncoming;
        }

        if (!incomingIsPlaceholder && normalizedIncoming.Length > normalizedCurrent.Length)
        {
            return normalizedIncoming;
        }

        return normalizedCurrent;
    }

    private static string? SelectPreferredOptional(string? current, string? incoming)
    {
        string? normalizedCurrent = string.IsNullOrWhiteSpace(current) ? null : current.Trim();
        string? normalizedIncoming = string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();

        if (normalizedIncoming is null)
        {
            return normalizedCurrent;
        }

        if (normalizedCurrent is null)
        {
            return normalizedIncoming;
        }

        return normalizedCurrent.Length >= normalizedIncoming.Length ? normalizedCurrent : normalizedIncoming;
    }

    private static bool LooksLikePdf(string url, string? mime)
    {
        if (!string.IsNullOrWhiteSpace(mime) && mime.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string path = uri.AbsolutePath;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        else if (url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? NormalizeNavigableUrl(string url, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(url) || depth > 2)
        {
            return url;
        }

        if (!TryCreateAbsoluteUri(url, out var uri))
        {
            // Attempt to coerce bare redirect paths from Google results into absolute URIs.
            if (url.StartsWith("/url", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate("https://www.google.com" + url, UriKind.Absolute, out var corrected))
            {
                uri = corrected;
            }
            else
            {
                return url;
            }
        }

        string host = uri.Host;
        string path = uri.AbsolutePath;

        if (IsGoogleWebHost(host))
        {
            if (path.Equals("/url", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQueryParameters(uri.Query);
                if (TryResolveTarget(query, ["url", "q"], out var candidate))
                {
                    return NormalizeNavigableUrl(candidate!, depth + 1);
                }
            }
            else if (path.Equals("/imgres", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQueryParameters(uri.Query);
                if (TryResolveTarget(query, ["imgurl", "imgrefurl"], out var imageTarget))
                {
                    return NormalizeNavigableUrl(imageTarget!, depth + 1);
                }
            }
        }
        else if (IsBingWebHost(host))
        {
            if (path.Equals("/ck/a", StringComparison.OrdinalIgnoreCase) || path.Equals("/r", StringComparison.OrdinalIgnoreCase))
            {
                var query = ParseQueryParameters(uri.Query);
                if (TryResolveTarget(query, ["u", "url"], out var targetUrl))
                {
                    return NormalizeNavigableUrl(targetUrl!, depth + 1);
                }
            }
        }

        return uri?.AbsoluteUri ?? url;
    }

    private static bool LooksLikeSearchHub(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (!TryCreateAbsoluteUri(url, out uri))
            {
                return false;
            }
        }

        string host = uri.Host;
        string path = uri.AbsolutePath;

        if (IsGoogleWebHost(host))
        {
            if (path is "/" or "/search" || path.StartsWith("/search", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/webhp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (IsBingWebHost(host))
        {
            if (path is "/" || path.StartsWith("/search", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGoogleWebHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().ToLowerInvariant();
        if (host.Equals("google.com", StringComparison.Ordinal) || host.Equals("www.google.com", StringComparison.Ordinal))
        {
            return true;
        }

        if (host.EndsWith(".google.com", StringComparison.Ordinal))
        {
            return true;
        }

        if (host.StartsWith("google.", StringComparison.Ordinal) || host.StartsWith("www.google.", StringComparison.Ordinal))
        {
            return true;
        }

        return host.Contains(".google.", StringComparison.Ordinal);
    }

    private static bool IsBingWebHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().ToLowerInvariant();
        if (host.Equals("bing.com", StringComparison.Ordinal) || host.Equals("www.bing.com", StringComparison.Ordinal))
        {
            return true;
        }

        if (host.EndsWith(".bing.com", StringComparison.Ordinal))
        {
            return true;
        }

        return host.StartsWith("www.bing.", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        string trimmed = query[0] == '?' ? query[1..] : query;
        foreach (string segment in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = segment.IndexOf('=');
            string key;
            string value;

            if (separator < 0)
            {
                key = WebUtility.UrlDecode(segment);
                value = string.Empty;
            }
            else
            {
                key = WebUtility.UrlDecode(segment[..separator]);
                value = WebUtility.UrlDecode(segment[(separator + 1)..]);
            }

            if (string.IsNullOrWhiteSpace(key) || result.ContainsKey(key))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static bool TryResolveTarget(Dictionary<string, string> parameters, ReadOnlySpan<string> preferredKeys, out string? target)
    {
        foreach (string key in preferredKeys)
        {
            if (parameters.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                string trimmed = value.Trim();
                if (TryCreateAbsoluteUri(trimmed, out var candidate))
                {
                    target = candidate.AbsoluteUri;
                    return true;
                }
            }
        }

        target = null;
        return false;
    }

    private static bool TryCreateAbsoluteUri(string value, out Uri uri)
    {
        uri = null!;

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            uri = absolute;
            return true;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = "https:" + trimmed;
        }
        else if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out absolute))
        {
            uri = absolute;
            return true;
        }

        return false;
    }

    private static FlaggedResourceType ParseResourceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FlaggedResourceType.Page;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "page" or "article" or "link" or "section" => FlaggedResourceType.Page,
            "file" or "document" or "pdf" or "report" => FlaggedResourceType.File,
            "download" or "binary" or "dataset" or "archive" => FlaggedResourceType.Download,
            _ => FlaggedResourceType.Page
        };
    }

    private static string ExtractExplorationSummary(IReadOnlyList<string> transcript)
    {
        if (transcript.Count == 0)
        {
            return string.Empty;
        }

        for (int index = transcript.Count - 1; index >= 0; index--)
        {
            string candidate = transcript[index];
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length > 40)
            {
                return candidate.Trim();
            }
        }

        string fallback = string.Join(" ", transcript.TakeLast(3).Where(static entry => !string.IsNullOrWhiteSpace(entry))).Trim();
        return fallback;
    }

    private static IReadOnlyList<string> ExtractExplorationFindings(
        IReadOnlyList<string> structuredFindings,
        string? summary,
        IReadOnlyList<string> transcript)
    {
        if (structuredFindings.Count > 0)
        {
            return structuredFindings;
        }

        static IReadOnlyList<string> ParseBulletLines(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            var items = new List<string>();
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string raw in lines)
            {
                string candidate = raw.Trim();
                if (candidate.StartsWith("- ", StringComparison.Ordinal) || candidate.StartsWith("•", StringComparison.Ordinal) || candidate.StartsWith("* ", StringComparison.Ordinal))
                {
                    candidate = candidate.TrimStart('-', '•', '*', ' ').Trim();
                }

                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string normalized = candidate.Length > 320 ? candidate[..320] + "…" : candidate;
                items.Add(normalized);
            }

            return items.Count == 0
                ? Array.Empty<string>()
                : items.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        IReadOnlyList<string> fromSummary = ParseBulletLines(summary);
        if (fromSummary.Count > 0)
        {
            return fromSummary;
        }

        foreach (string segment in transcript.Reverse())
        {
            IReadOnlyList<string> parsed = ParseBulletLines(segment);
            if (parsed.Count > 0)
            {
                return parsed;
            }
        }

        return Array.Empty<string>();
    }

    private async Task<string?> CapturePageSynopsisAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return null;
        }

        try
        {
            const string script = @"() => {
                const candidates = [
                    document.querySelector('article'),
                    document.querySelector('main'),
                    document.querySelector('#mw-content-text'),
                    document.querySelector('.mw-parser-output')
                ].filter(Boolean);

                const seen = new Set();

                for (const root of candidates) {
                    if (!root) {
                        continue;
                    }

                    const paragraphs = Array.from(root.querySelectorAll('p'))
                        .map(p => (p.innerText || '').trim())
                        .filter(text => text.length > 0);

                    if (paragraphs.length === 0) {
                        continue;
                    }

                    const unique = [];
                    for (const paragraph of paragraphs) {
                        if (seen.has(paragraph)) {
                            continue;
                        }
                        seen.add(paragraph);
                        unique.push(paragraph);
                    }

                    if (unique.length > 0) {
                        return unique.slice(0, 3).join('\n');
                    }
                }

                return null;
            }";

            string? raw = await _page.EvaluateAsync<string?>(script).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string[] lines = raw
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (lines.Length == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.AppendLine("Key observations gathered from page text:");

            int count = 0;
            foreach (string line in lines)
            {
                if (count >= 3)
                {
                    break;
                }

                string cleaned = line.Trim();
                if (cleaned.Length == 0)
                {
                    continue;
                }

                if (!char.IsUpper(cleaned[0]))
                {
                    cleaned = char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
                }

                if (!cleaned.EndsWith(".", StringComparison.Ordinal))
                {
                    cleaned += ".";
                }

                builder.Append("- ");
                builder.AppendLine(cleaned);
                count++;
            }

            return count == 0 ? null : builder.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to capture fallback page summary.");
            return null;
        }
    }

    private sealed record ExplorationStructuredOutput(string? Summary, IReadOnlyList<string> Findings, IReadOnlyList<FlaggedResource> FlaggedResources, string? RawJson);

    private sealed class ExplorationPayload
    {
        public string? Summary { get; set; }

        public List<string>? Findings { get; set; }

        public List<ExplorationPayloadResource>? Flagged { get; set; }
    }

    private sealed class ExplorationPayloadResource
    {
        public string? Type { get; set; }

        public string? Title { get; set; }

        public string? Url { get; set; }

        public string? MimeType { get; set; }

        public string? Notes { get; set; }
    }

}

public sealed class ComputerUseSearchBlockedException : InvalidOperationException
{
    public ComputerUseSearchBlockedException(string message)
        : base(message)
    {
    }
}

public sealed class ComputerUseOperationTimeoutException : TimeoutException
{
    public ComputerUseOperationTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ComputerUseSummaryFunctionException : InvalidOperationException
{
    public ComputerUseSummaryFunctionException(string responseContent)
        : base($"Summary function is not supported by the deployment response: {responseContent}")
    {
        ResponseContent = responseContent;
    }

    public string ResponseContent { get; }
}

public sealed class ComputerUseFlaggingException : InvalidOperationException
{
    public ComputerUseFlaggingException(string message)
        : base(message)
    {
    }
}

public sealed record ComputerUseSearchResult(
    IReadOnlyList<ComputerUseSearchResultItem> Items,
    IReadOnlyList<string> Transcript,
    string? FinalUrl);

public sealed record ComputerUseSearchResultItem(string Title, string Url, string Snippet);

public sealed record ComputerUseExplorationResult(
    string RequestedUrl,
    string? FinalUrl,
    string? PageTitle,
    string? Summary,
    string? StructuredSummaryJson,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Transcript,
    IReadOnlyList<FlaggedResource> FlaggedResources);




