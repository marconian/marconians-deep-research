using System;
using System.Collections.Generic;
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
    private const int DisplayWidth = 1280;
    private const int DisplayHeight = 720;
    private const int MaxIterations = 8;

    private const string SearchInstructions = "You control a Chromium browser. Execute actions one at a time, reviewing a screenshot after each step. Stop once Google search results are visible so the host application can read them.";

    private const string ExplorationInstructions = """
You control a Chromium browser. Navigate the page, scroll to gather supporting evidence, follow relevant outbound links, and stop once you can provide a concise summary of the key takeaways.

When you stop, respond with exactly one JSON object on the final turn matching this schema:
{"summary":"...","flagged":[{"type":"page|file|download","title":"...","url":"...","notes":"optional context","mimeType":"optional mime"}]}

Flag any resources that should be revisited (interesting subpages, downloadable files, data sources). Emit an empty array when nothing is flagged. Do not include text outside the JSON object.
""";

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

    public ComputerUseSearchService(
        string endpoint,
        string apiKey,
        string deployment,
        HttpClient httpClient,
        ILogger<ComputerUseSearchService>? logger = null)
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

            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { $"--window-size={DisplayWidth},{DisplayHeight}", "--disable-extensions" }
            }).ConfigureAwait(false);

            _context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = DisplayWidth, Height = DisplayHeight },
                AcceptDownloads = true
            }).ConfigureAwait(false);

            _page = await _context.NewPageAsync().ConfigureAwait(false);
            // Use Google NCR (no country redirect) to reduce regional consent variance.
            await _page.GotoAsync("https://www.google.com/ncr", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<ComputerUseSearchResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Start a new session for this query to correlate artifacts.
            _currentSessionId = Guid.NewGuid().ToString("N");
            _screenshotSequence = 0;
            InitializeTimeline(query);
            if (_page is null)
            {
                throw new InvalidOperationException("Computer-use browser page was not initialized.");
            }

            await NavigateToStartAsync(cancellationToken).ConfigureAwait(false);
            var transcript = new List<string>();

            ComputerUseResponseState response = await SendInitialRequestAsync(query, transcript, cancellationToken).ConfigureAwait(false);
            int iteration = 0;

            while (response.Call is not null && iteration < MaxIterations)
            {
                iteration++;
                await HandleComputerCallAsync(response.Call, cancellationToken).ConfigureAwait(false);
                response = await SendFollowupRequestAsync(response, transcript, cancellationToken, SearchInstructions).ConfigureAwait(false);

                if (response.Call is null || response.Completed)
                {
                    break;
                }
            }

            if (iteration >= MaxIterations && response.Call is not null)
            {
                _logger.LogWarning("Computer-use search for query '{Query}' reached iteration limit before completion.", query);
            }

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
        finally
        {
            try
            {
                await PersistTimelineAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist computer-use timeline for query '{Query}'.", query);
            }

            _operationLock.Release();
        }
    }

    private async Task NavigateToStartAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

    await _page.GotoAsync("https://www.google.com/ncr", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        await DismissConsentAsync(cancellationToken).ConfigureAwait(false);
        await _page.BringToFrontAsync().ConfigureAwait(false);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ComputerUseResponseState> SendInitialRequestAsync(string query, List<string> transcript, CancellationToken cancellationToken)
    {
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

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private JsonArray BuildToolsArray()
    {
        return new JsonArray
        {
            new JsonObject
            {
                ["type"] = "computer_use_preview",
                ["display_width"] = DisplayWidth,
                ["display_height"] = DisplayHeight,
                ["environment"] = "browser"
            }
        };
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
                        transcript.Add(text.Trim());
                    }
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
            }
        }

        return new ComputerUseResponseState(responseId, call, completed);
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
                if (coordinates is not null)
                {
                    await _page.Mouse.DblClickAsync(coordinates.Value.X, coordinates.Value.Y).ConfigureAwait(false);
                    await WaitForPotentialNavigationAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
            case "scroll":
                await HandleScrollAsync(action, coordinates, cancellationToken).ConfigureAwait(false);
                break;
            case "move":
                if (coordinates is not null)
                {
                    await _page.Mouse.MoveAsync(coordinates.Value.X, coordinates.Value.Y).ConfigureAwait(false);
                }
                break;
            case "keypress":
            case "key":
                await HandleKeyPressAsync(action.Keys, cancellationToken).ConfigureAwait(false);
                break;
            case "type":
                if (!string.IsNullOrWhiteSpace(action.Text))
                {
                    await _page.Keyboard.TypeAsync(action.Text, new KeyboardTypeOptions { Delay = 25 }).ConfigureAwait(false);
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
            await _page.Mouse.WheelAsync(0, (float)action.ScrollY).ConfigureAwait(false);
            await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (coordinates is null)
        {
            return;
        }

        await _page.Mouse.ClickAsync(coordinates.Value.X, coordinates.Value.Y, new MouseClickOptions { Button = MapMouseButton(button) }).ConfigureAwait(false);
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
            await _page.Mouse.MoveAsync(coordinates.Value.X, coordinates.Value.Y).ConfigureAwait(false);
        }

        await _page.Mouse.WheelAsync((float)action.ScrollX, (float)action.ScrollY).ConfigureAwait(false);
        await WaitForVisualStabilityAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleKeyPressAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (_page is null || keys.Count == 0)
        {
            return;
        }

        var translated = keys
            .Select(key => TranslateKey(key))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (translated.Length == 0)
        {
            return;
        }

        if (translated.Length > 1)
        {
            foreach (string key in translated)
            {
                await _page.Keyboard.DownAsync(key).ConfigureAwait(false);
            }

            await Task.Delay(75).ConfigureAwait(false);

            foreach (string key in translated.Reverse())
            {
                await _page.Keyboard.UpAsync(key).ConfigureAwait(false);
            }
        }
        else
        {
            await _page.Keyboard.PressAsync(translated[0]).ConfigureAwait(false);
        }

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
            var items = JsonSerializer.Deserialize<List<ComputerUseSearchResultItem>>(json, SerializerOptions);
            var filtered = items?.Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Url)).ToList()
                           ?? new List<ComputerUseSearchResultItem>();

            if (filtered.Count == 0)
            {
                string transcriptSummary = transcript.Count == 0 ? "<empty>" : string.Join(" | ", transcript.Take(8));
                string? currentUrl = _page?.Url;
                _logger.LogWarning("Computer-use DOM extraction produced no items for query '{Query}'. CurrentUrl={Url}. Transcript={Transcript}", query, currentUrl, transcriptSummary);
                await PersistDebugArtifactsAsync(query, json, cancellationToken).ConfigureAwait(false);
            }

            return filtered;
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

        try
        {
            await _page.WaitForSelectorAsync("li.b_algo, div.g", new PageWaitForSelectorOptions { Timeout = 4000 }).ConfigureAwait(false);
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

    private static (float X, float Y)? ClampCoordinates(double? x, double? y)
    {
        if (x is null || y is null)
        {
            return null;
        }

        float clampedX = (float)Math.Clamp(x.Value, 0, DisplayWidth);
        float clampedY = (float)Math.Clamp(y.Value, 0, DisplayHeight);
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
                ["sessionId"] = _currentSessionId
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
            var payload = JsonSerializer.Serialize(_timelineEntries, SerializerOptions);
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

    public async ValueTask DisposeAsync()
    {
        _operationLock.Dispose();
        _initializationLock.Dispose();

        if (_page is not null)
        {
            try
            {
                await _page.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_context is not null)
        {
            try
            {
                await _context.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_browser is not null)
        {
            try
            {
                await _browser.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_playwright is not null)
        {
            try
            {
                _playwright.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed record ComputerUseResponseState(string? ResponseId, ComputerCall? Call, bool Completed);

    private sealed record ComputerCall(string CallId, ComputerUseAction Action, IReadOnlyList<ComputerUseSafetyCheck> PendingSafetyChecks);

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

    private readonly record struct ViewportSnapshot(double ScrollHeight, int PendingImages);

    public async Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _currentSessionId = Guid.NewGuid().ToString("N");
            _screenshotSequence = 0;
            InitializeTimeline($"explore::{url}");
            if (_page is null)
            {
                throw new InvalidOperationException("Computer-use browser page was not initialized.");
            }

            RecordTimelineEvent("exploration_start", new Dictionary<string, object?>
            {
                ["url"] = url,
                ["objective"] = objective
            });

            await _page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            }).ConfigureAwait(false);
            await DismissConsentAsync(cancellationToken).ConfigureAwait(false);
            await ThrowIfCaptchaDetectedAsync(url, cancellationToken).ConfigureAwait(false);
            await _page.BringToFrontAsync().ConfigureAwait(false);
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);

            var transcript = new List<string>();
            ComputerUseResponseState response = await SendInitialExplorationRequestAsync(url, objective, transcript, cancellationToken).ConfigureAwait(false);
            int iteration = 0;

            while (response.Call is not null && iteration < MaxIterations)
            {
                iteration++;
                await HandleComputerCallAsync(response.Call, cancellationToken).ConfigureAwait(false);
                response = await SendFollowupRequestAsync(response, transcript, cancellationToken, ExplorationInstructions).ConfigureAwait(false);

                if (response.Call is null || response.Completed)
                {
                    break;
                }
            }

            if (iteration >= MaxIterations && response.Call is not null)
            {
                _logger.LogWarning("Computer-use exploration for url '{Url}' reached iteration limit before completion.", url);
            }

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

            ExplorationStructuredOutput structured = ParseExplorationStructuredOutput(transcript);
            string summary = !string.IsNullOrWhiteSpace(structured.Summary)
                ? structured.Summary!
                : ExtractExplorationSummary(transcript);

            if (structured.FlaggedResources.Count > 0)
            {
                RecordTimelineEvent("flagged_resources", new Dictionary<string, object?>
                {
                    ["count"] = structured.FlaggedResources.Count,
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

            RecordTimelineEvent("exploration_complete", new Dictionary<string, object?>
            {
                ["requestedUrl"] = url,
                ["finalUrl"] = finalUrl,
                ["summary"] = summary
            });

            return new ComputerUseExplorationResult(url, finalUrl, pageTitle, summary, transcript.ToArray(), structured.FlaggedResources);
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
        finally
        {
            try
            {
                await PersistTimelineAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to persist computer-use timeline for exploration '{Url}'.", url);
            }

            try
            {
                await NavigateToStartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to reset browser after exploration session.");
            }

            _operationLock.Release();
        }
    }

    private async Task<ComputerUseResponseState> SendInitialExplorationRequestAsync(string targetUrl, string? objective, List<string> transcript, CancellationToken cancellationToken)
    {
        string screenshot = await CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        string objectiveClause = string.IsNullOrWhiteSpace(objective)
            ? "Review the currently open page and capture the most important insights."
            : $"Review the currently open page and capture information relevant to \"{objective}\".";

        string userInstruction = string.Concat(objectiveClause, " Scroll as needed before summarizing your findings. The requested page is ", targetUrl, ". Provide a concise summary with bullet points and note any important sections or outbound links.");

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
                ["instructions"] = ExplorationInstructions,
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

    private ExplorationStructuredOutput ParseExplorationStructuredOutput(IReadOnlyList<string> transcript)
    {
        if (transcript.Count == 0)
        {
            return new ExplorationStructuredOutput(null, Array.Empty<FlaggedResource>());
        }

        for (int index = transcript.Count - 1; index >= 0; index--)
        {
            string entry = transcript[index];
            if (!TryParseExplorationPayload(entry, out var payload))
            {
                continue;
            }

            List<FlaggedResource> flagged = payload.Flagged is null
                ? new List<FlaggedResource>()
                : payload.Flagged
                    .Select(TryConvertResource)
                    .Where(static resource => resource is not null)
                    .Select(static resource => resource!)
                    .DistinctBy(static resource => resource.Url, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return new ExplorationStructuredOutput(payload.Summary, flagged);
        }

        return new ExplorationStructuredOutput(null, Array.Empty<FlaggedResource>());
    }

    private bool TryParseExplorationPayload(string entry, out ExplorationPayload payload)
    {
        payload = new ExplorationPayload();
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

            if (string.IsNullOrWhiteSpace(parsed.Summary) && (parsed.Flagged is null || parsed.Flagged.Count == 0))
            {
                return false;
            }

            payload = parsed;
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

    private sealed record ExplorationStructuredOutput(string? Summary, IReadOnlyList<FlaggedResource> FlaggedResources);

    private sealed class ExplorationPayload
    {
        public string? Summary { get; set; }

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
    IReadOnlyList<string> Transcript,
    IReadOnlyList<FlaggedResource> FlaggedResources);




