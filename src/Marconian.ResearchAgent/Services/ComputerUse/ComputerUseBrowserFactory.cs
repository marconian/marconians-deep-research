using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using TimeZoneConverter;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class ComputerUseBrowserFactory
{
    private static readonly string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.6613.137 Safari/537.36";

    private readonly ComputerUseOptions _options;
    private readonly ILogger _logger;
    private readonly ComputerUseProxyManager _proxyManager;

    public ComputerUseBrowserFactory(ComputerUseOptions options, ILogger logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _proxyManager = new ComputerUseProxyManager(options.Proxy);
    }

    public async Task<ComputerUseBrowserSession> CreateAsync(CancellationToken cancellationToken)
    {
        IPlaywright playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        ComputerUseProxySelection? proxySelection = _proxyManager.TryAcquire();

        string profileDirectory = EnsureProfileDirectory();
        string locale = proxySelection?.Endpoint.PreferredLocale ?? _options.PreferredLocale ?? "en-US";
        string requestedTimeZone = proxySelection?.Endpoint.PreferredTimeZoneId ?? _options.PreferredTimeZoneId ?? TimeZoneInfo.Local.Id;
        string timeZone = NormalizeTimeZoneId(requestedTimeZone);
    var viewport = new ViewportSize { Width = _options.Viewport.Width, Height = _options.Viewport.Height };
        string userAgent = string.IsNullOrWhiteSpace(_options.UserAgent) ? DefaultUserAgent : _options.UserAgent!;
    var extraArgs = BuildLaunchArguments(locale);

        Geolocation? geolocation = null;
        if (proxySelection?.Endpoint.Latitude is double lat && proxySelection.Endpoint.Longitude is double lon)
        {
            geolocation = new Geolocation { Latitude = (float)lat, Longitude = (float)lon, Accuracy = 20 };
        }

        IBrowser? browser = null;
        IBrowserContext context;

        if (_options.UsePersistentContext)
        {
            Directory.CreateDirectory(profileDirectory);
            var launchOptions = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = _options.Headless,
                Args = extraArgs.ToArray(),
                IgnoreDefaultArgs = new[] { "--enable-automation" },
                ViewportSize = viewport,
                Locale = locale,
                TimezoneId = timeZone,
                UserAgent = userAgent,
                ColorScheme = ColorScheme.Light,
                DeviceScaleFactor = _options.Viewport.DeviceScaleFactor,
                Proxy = proxySelection?.Proxy,
                AcceptDownloads = true
            };

            if (geolocation is not null)
            {
                launchOptions.Geolocation = geolocation;
                launchOptions.Permissions = new[] { "geolocation" };
            }

            context = await playwright.Chromium.LaunchPersistentContextAsync(profileDirectory, launchOptions).ConfigureAwait(false);
            browser = context.Browser;
        }
        else
        {
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless,
                Args = extraArgs.ToArray(),
                IgnoreDefaultArgs = new[] { "--enable-automation" }
            };

            browser = await playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);

            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = viewport,
                Locale = locale,
                TimezoneId = timeZone,
                UserAgent = userAgent,
                Proxy = proxySelection?.Proxy,
                ColorScheme = ColorScheme.Light,
                DeviceScaleFactor = _options.Viewport.DeviceScaleFactor,
                AcceptDownloads = true
            };

            if (geolocation is not null)
            {
                contextOptions.Geolocation = geolocation;
                contextOptions.Permissions = new[] { "geolocation" };
            }

            context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);
        }

        var stealthProfile = new StealthProfile(_options.Stealth, _logger);
        await stealthProfile.ApplyAsync(context, cancellationToken).ConfigureAwait(false);

        IPage page = await context.NewPageAsync().ConfigureAwait(false);
        await ApplyBaselineEmulationAsync(page).ConfigureAwait(false);

        _logger.LogInformation("Initialized Playwright session (Headless={Headless}, Persistent={Persistent}, Locale={Locale}, TimeZone={TimeZone}, RequestedTimeZone={RequestedTimeZone}, Proxy={Proxy}).",
            _options.Headless,
            _options.UsePersistentContext,
            locale,
            timeZone,
            string.IsNullOrWhiteSpace(requestedTimeZone) ? "(default)" : requestedTimeZone,
            proxySelection?.Endpoint.Uri ?? "none");

    return new ComputerUseBrowserSession(playwright, browser, context, page, stealthProfile, proxySelection, timeZone);
    }

    private string NormalizeTimeZoneId(string? timeZoneId)
    {
        const string Fallback = "UTC";

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return Fallback;
        }

        string trimmed = timeZoneId.Trim();

        try
        {
            _ = TZConvert.IanaToWindows(trimmed);
            return trimmed;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        try
        {
            return TZConvert.WindowsToIana(trimmed);
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        try
        {
            TimeZoneInfo info = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
            try
            {
                return TZConvert.WindowsToIana(info.Id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        _logger.LogWarning("Unsupported timezone '{TimeZone}', falling back to UTC.", trimmed);
        return Fallback;
    }

    private string EnsureProfileDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.UserDataDirectory))
        {
            return Path.Combine(Directory.GetCurrentDirectory(), "debug", "cache", "computer-use-profile");
        }

        return _options.UserDataDirectory!;
    }

    private static async Task ApplyBaselineEmulationAsync(IPage page)
    {
        await page.AddInitScriptAsync(@"Object.defineProperty(navigator, 'maxTouchPoints', { get: () => 0 });").ConfigureAwait(false);
        await page.AddInitScriptAsync(@"Object.defineProperty(navigator, 'platform', { get: () => 'Win32' });").ConfigureAwait(false);
        await page.AddInitScriptAsync(@"Object.defineProperty(navigator, 'language', { get: () => 'en-US' });").ConfigureAwait(false);
    }

    private List<string> BuildLaunchArguments(string locale)
    {
        string normalizedLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Replace('_', '-');
        var args = new List<string>
        {
            $"--window-size={_options.Viewport.Width},{_options.Viewport.Height}",
            "--disable-blink-features=AutomationControlled",
            "--disable-features=AutomationControlled",
            "--disable-features=IsolateOrigins,site-per-process",
            "--lang=" + normalizedLocale,
            "--password-store=basic",
            "--use-mock-keychain",
            "--no-default-browser-check",
            "--enable-blink-features=IdleDetection",
            "--start-maximized"
        };

        if (!_options.Headless)
        {
            args.Add("--hide-crash-restore-bubble");
        }

        if (!string.IsNullOrWhiteSpace(_options.Driver.PatchedDriverPath))
        {
            _logger.LogInformation("Using patched Playwright driver at {Path}.", _options.Driver.PatchedDriverPath);
        }

        return args;
    }
}

public sealed record ComputerUseBrowserSession(
    IPlaywright Playwright,
    IBrowser? Browser,
    IBrowserContext Context,
    IPage Page,
    StealthProfile Stealth,
    ComputerUseProxySelection? ProxySelection,
    string TimeZoneId) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        Stealth.Dispose();
        await Page.CloseAsync().ConfigureAwait(false);
        await Context.CloseAsync().ConfigureAwait(false);
        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }
        Playwright.Dispose();
    }
}
