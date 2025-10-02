using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public static class ComputerUseRuntimeProbe
{
    private static readonly SemaphoreSlim _probeLock = new(1, 1);
    private static bool? _cachedReady;
    private static string? _cachedReason;

    public static async Task<ComputerUseReadinessResult> EnsureReadyAsync(ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (_cachedReady.HasValue)
        {
            return new ComputerUseReadinessResult(_cachedReady.Value, _cachedReason);
        }

        ILogger effectiveLogger = logger ?? NullLogger.Instance;

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedReady.HasValue)
            {
                return new ComputerUseReadinessResult(_cachedReady.Value, _cachedReason);
            }

            try
            {
                int installExitCode = Microsoft.Playwright.Program.Main(new[] { "install" });
                if (installExitCode != 0)
                {
                    string reason = $"Playwright browser installation exited with code {installExitCode}.";
                    effectiveLogger.LogWarning(
                        "Computer-use readiness probe failed: {Reason}. Run 'pwsh tests/Marconian.ResearchAgent.Tests/bin/Debug/net9.0/playwright.ps1 install' to retry.",
                        reason);
                    _cachedReady = false;
                    _cachedReason = reason;
                    return new ComputerUseReadinessResult(_cachedReady.Value, _cachedReason);
                }

                using IPlaywright playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                string? executablePath = playwright.Chromium.ExecutablePath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    string reason = "Playwright Chromium executable was not found after installation.";
                    effectiveLogger.LogWarning(
                        "Computer-use readiness probe failed: {Reason}. Install browsers with 'pwsh bin/Debug/net9.0/playwright.ps1 install'.",
                        reason);
                    _cachedReady = false;
                    _cachedReason = reason;
                }
                else
                {
                    _cachedReady = true;
                    _cachedReason = null;
                }
            }
            catch (PlaywrightException ex)
            {
                string reason = $"Playwright failed to install or initialize: {ex.Message}";
                effectiveLogger.LogWarning(ex, "Computer-use readiness probe failed. Playwright initialization error.");
                _cachedReady = false;
                _cachedReason = reason;
            }
            catch (IOException ex)
            {
                string reason = $"Filesystem error preparing Playwright browsers: {ex.Message}";
                effectiveLogger.LogWarning(ex, "Computer-use readiness probe failed due to IO error.");
                _cachedReady = false;
                _cachedReason = reason;
            }
            catch (UnauthorizedAccessException ex)
            {
                string reason = $"Insufficient permissions preparing Playwright browsers: {ex.Message}";
                effectiveLogger.LogWarning(ex, "Computer-use readiness probe failed due to permission issues.");
                _cachedReady = false;
                _cachedReason = reason;
            }
            catch (System.Exception ex)
            {
                string reason = $"Unexpected error preparing Playwright browsers: {ex.Message}";
                effectiveLogger.LogWarning(ex, "Computer-use readiness probe failed unexpectedly.");
                _cachedReady = false;
                _cachedReason = reason;
            }
        }
        finally
        {
            _probeLock.Release();
        }

        return new ComputerUseReadinessResult(_cachedReady!.Value, _cachedReason);
    }
}

public sealed record ComputerUseReadinessResult(bool IsReady, string? FailureReason);
