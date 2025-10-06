using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class StealthProfile : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> DefaultClientHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Sec-CH-UA"] = "\"Chromium\";v=\"128\", \"Google Chrome\";v=\"128\", \"Not;A=Brand\";v=\"99\"",
        ["Sec-CH-UA-Mobile"] = "?0",
        ["Sec-CH-UA-Platform"] = "\"Windows\""
    };

    private readonly ComputerUseStealthOptions _options;
    private readonly ILogger? _logger;
    private IBrowserContext? _context;

    public StealthProfile(ComputerUseStealthOptions options, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    public async Task ApplyAsync(IBrowserContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.EnableScriptPatches)
        {
            await context.AddInitScriptAsync(StealthBootstrapScript).ConfigureAwait(false);
        }

        await context.SetExtraHTTPHeadersAsync(DefaultClientHints).ConfigureAwait(false);

        if (_options.EnableConsoleQuiets)
        {
            _context = context;
            context.Console += HandleConsole;
        }
    }

    public void Dispose()
    {
        if (_context is not null && _options.EnableConsoleQuiets)
        {
            _context.Console -= HandleConsole;
            _context = null;
        }
    }

    private void HandleConsole(object? sender, IConsoleMessage message)
    {
        if (message.Type == "warning" || message.Text.Contains("webdriver", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("Suppressed console message from page: {Message}", message.Text);
        }
    }

    private const string StealthBootstrapScript = @"() => {
        const patch = () => {
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });

            if (!window.chrome) {
                Object.defineProperty(window, 'chrome', { value: { runtime: {} } });
            }

            const originalQuery = navigator.permissions.query;
            navigator.permissions.query = function(parameters) {
                if (parameters && parameters.name === 'notifications') {
                    return Promise.resolve({ state: Notification.permission });
                }
                return originalQuery.call(this, parameters);
            };

            const pluginArray = [
                { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
                { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '' },
                { name: 'Native Client', filename: 'internal-nacl-plugin', description: '' }
            ];
            Object.defineProperty(navigator, 'plugins', { get: () => pluginArray });
            Object.defineProperty(navigator, 'mimeTypes', { get: () => pluginArray.map(p => ({ type: 'application/pdf', suffixes: 'pdf', description: p.description })) });

            const languages = navigator.languages && navigator.languages.length ? navigator.languages : ['en-US', 'en'];
            Object.defineProperty(navigator, 'languages', { get: () => languages });

            const hardwareConcurrency = navigator.hardwareConcurrency || 8;
            Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => hardwareConcurrency });

            const getParameter = WebGLRenderingContext.prototype.getParameter;
            WebGLRenderingContext.prototype.getParameter = function(parameter) {
                if (parameter === 37445) {
                    return 'Intel Inc.';
                }
                if (parameter === 37446) {
                    return 'Intel(R) Iris(R) Graphics';
                }
                return getParameter.call(this, parameter);
            };

            const getParameter2 = WebGL2RenderingContext && WebGL2RenderingContext.prototype.getParameter;
            if (getParameter2) {
                WebGL2RenderingContext.prototype.getParameter = function(parameter) {
                    if (parameter === 37445) {
                        return 'Intel Inc.';
                    }
                    if (parameter === 37446) {
                        return 'Intel(R) Iris(R) Graphics';
                    }
                    return getParameter2.call(this, parameter);
                };
            }

            if (navigator.deviceMemory === undefined) {
                Object.defineProperty(navigator, 'deviceMemory', { get: () => 8 });
            }
        };

        try {
            patch();
        } catch (error) {
            console.debug('Stealth bootstrap failed', error);
        }
    }";
}
