using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class ComputerUseSearchService : IAsyncDisposable
{
    private const string ResponsesApiVersion = "2024-08-01-preview";
    private const int DisplayWidth = 1280;
    private const int DisplayHeight = 720;
    private const int MaxIterations = 8;

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
            await _page.GotoAsync("https://www.google.com", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
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
                response = await SendFollowupRequestAsync(response, transcript, cancellationToken).ConfigureAwait(false);

                if (response.Call is null || response.Completed)
                {
                    break;
                }
            }

            if (iteration >= MaxIterations && response.Call is not null)
            {
                _logger.LogWarning("Computer-use search for query '{Query}' reached iteration limit before completion.", query);
            }

            IReadOnlyList<ComputerUseSearchResultItem> items = await ExtractResultsAsync(cancellationToken).ConfigureAwait(false);
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
            _operationLock.Release();
        }
    }

    private async Task NavigateToStartAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return;
        }

        await _page.GotoAsync("https://www.google.com", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
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
                            ["text"] = $"Use Google Search to gather the top five organic results for \"{query}\". Avoid ads."
                        },
                        new JsonObject
                        {
                            ["type"] = "input_image",
                            ["image_url"] = $"data:image/png;base64,{screenshot}"
                        }
                    }
                }
            },
            ["instructions"] = "You control a Chromium browser. Execute actions one at a time, reviewing a screenshot after each step. Stop once Google results are visible so the host application can read them.",
            ["tools"] = BuildToolsArray(),
            ["reasoning"] = new JsonObject { ["generate_summary"] = "concise" },
            ["temperature"] = 0.2,
            ["top_p"] = 0.8,
            ["truncation"] = "auto"
        };

        using JsonDocument response = await SendRequestAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseResponse(response, transcript);
    }

    private async Task<ComputerUseResponseState> SendFollowupRequestAsync(ComputerUseResponseState previous, List<string> transcript, CancellationToken cancellationToken)
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
        string uri = $"{_endpoint}openai/deployments/{_deployment}/responses?api-version={ResponsesApiVersion}";
        string body = payload.ToJsonString(SerializerOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _apiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Computer-use responses call failed with status {Status}: {Body}", response.StatusCode, responseContent);
            response.EnsureSuccessStatusCode();
        }

        return JsonDocument.Parse(responseContent);
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
                await HandleScrollAsync(action, coordinates).ConfigureAwait(false);
                break;
            case "move":
                if (coordinates is not null)
                {
                    await _page.Mouse.MoveAsync(coordinates.Value.X, coordinates.Value.Y).ConfigureAwait(false);
                }
                break;
            case "keypress":
            case "key":
                await HandleKeyPressAsync(action.Keys).ConfigureAwait(false);
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
            return;
        }

        if (button == "forward")
        {
            await _page.GoForwardAsync().ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (button == "wheel")
        {
            await _page.Mouse.WheelAsync(0, (float)action.ScrollY).ConfigureAwait(false);
            return;
        }

        if (coordinates is null)
        {
            return;
        }

        await _page.Mouse.ClickAsync(coordinates.Value.X, coordinates.Value.Y, new MouseClickOptions { Button = MapMouseButton(button) }).ConfigureAwait(false);
        await WaitForPotentialNavigationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleScrollAsync(ComputerUseAction action, (float X, float Y)? coordinates)
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
    }

    private async Task HandleKeyPressAsync(IReadOnlyList<string> keys)
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

    private async Task<IReadOnlyList<ComputerUseSearchResultItem>> ExtractResultsAsync(CancellationToken cancellationToken)
    {
        if (_page is null)
        {
            return Array.Empty<ComputerUseSearchResultItem>();
        }

        const string script = @"(() => {
            const results = [];
            const candidates = document.querySelectorAll('div.g');
            for (const candidate of candidates) {
                const titleNode = candidate.querySelector('h3');
                const linkNode = candidate.querySelector('a');
                if (!titleNode || !linkNode) {
                    continue;
                }
                const title = titleNode.innerText.trim();
                const url = linkNode.href;
                if (!title || !url) {
                    continue;
                }
                const snippetNode = candidate.querySelector('div.VwiC3b') || candidate.querySelector('span.aCOpRe') || candidate.querySelector('.MUxGbd') || candidate.querySelector('.zCubwf');
                const snippet = snippetNode ? snippetNode.innerText.trim() : '';
                results.push({ title, url, snippet });
                if (results.length >= 5) {
                    break;
                }
            }
            if (results.length === 0) {
                const fallbacks = document.querySelectorAll('a h3');
                for (const titleNode of fallbacks) {
                    const anchor = titleNode.closest('a');
                    if (!anchor) {
                        continue;
                    }
                    const title = titleNode.innerText.trim();
                    const url = anchor.href;
                    if (!title || !url) {
                        continue;
                    }
                    let snippet = '';
                    const container = anchor.closest('div.g') || anchor.parentElement;
                    if (container) {
                        const snippetNode = container.querySelector('div.VwiC3b') || container.querySelector('span.aCOpRe') || container.querySelector('.MUxGbd');
                        if (snippetNode) {
                            snippet = snippetNode.innerText.trim();
                        }
                    }
                    results.push({ title, url, snippet });
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
            return items?.Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Url)).ToList()
                   ?? new List<ComputerUseSearchResultItem>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse search results DOM.");
            return Array.Empty<ComputerUseSearchResultItem>();
        }
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
}

public sealed record ComputerUseSearchResult(
    IReadOnlyList<ComputerUseSearchResultItem> Items,
    IReadOnlyList<string> Transcript,
    string? FinalUrl);

public sealed record ComputerUseSearchResultItem(string Title, string Url, string Snippet);




