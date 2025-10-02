using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Services.ComputerUse;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Integration;

[Explicit("Requires Azure OpenAI computer-use deployment, API key, and Playwright browsers.")]
[Parallelizable(ParallelScope.Self)]
public sealed class ComputerUseSearchServiceIntegrationTests
{
    [Test]
    public async Task SearchAsync_ShouldSurfaceDiagnostics()
    {
        string? endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        string? apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        string? deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_COMPUTER_USE_DEPLOYMENT");

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey) ||
            string.IsNullOrWhiteSpace(deployment))
        {
            Assert.Ignore("Azure OpenAI computer-use credentials are not configured for integration testing.");
        }

        using var provider = new TestLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(provider);
        });

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(40)
        };

        await using var service = new ComputerUseSearchService(endpoint, apiKey, deployment, httpClient, loggerFactory.CreateLogger<ComputerUseSearchService>());

        ComputerUseSearchResult? result = null;
        Exception? failure = null;

        try
        {
            result = await service.SearchAsync("Computer-use integration diagnostics", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            failure = ex;
        }

        foreach (string entry in provider.Entries)
        {
            TestContext.Progress.WriteLine(entry);
        }

        if (failure is HttpRequestException httpEx)
        {
            TestContext.Progress.WriteLine($"HTTP failure status: {(httpEx.StatusCode.HasValue ? (int)httpEx.StatusCode.Value : 0)} {httpEx.StatusCode}");
            TestContext.Progress.WriteLine($"Exception message: {httpEx.Message}");

            Assert.That(provider.Entries.Any(entry =>
                entry.Contains("Computer-use responses call failed", StringComparison.OrdinalIgnoreCase) ||
                entry.Contains("Computer-use request succeeded after falling back", StringComparison.OrdinalIgnoreCase)),
                "Expected the computer-use service to log diagnostic information about responses calls.");

            return;
        }

        if (failure is InvalidOperationException invalidOperation)
        {
            TestContext.Progress.WriteLine($"Computer-use request exhausted all endpoints: {invalidOperation.Message}");

            Assert.That(provider.Entries.Any(entry =>
                entry.Contains("Computer-use responses call failed", StringComparison.OrdinalIgnoreCase)),
                "Expected diagnostics for responses call failures when all endpoints fail.");

            return;
        }

        Assert.That(result, Is.Not.Null, "Computer-use search should produce a result when no exception occurs.");

        TestContext.Progress.WriteLine($"Computer-use returned {result!.Items.Count} organic result(s).");
        int index = 0;
        foreach (var item in result.Items)
        {
            TestContext.Progress.WriteLine($"[{++index}] {item.Title}\n\t{item.Url}\n\t{item.Snippet}");
        }

        if (result.Transcript.Count > 0)
        {
            TestContext.Progress.WriteLine("Transcript excerpt:");
            foreach (string line in result.Transcript)
            {
                TestContext.Progress.WriteLine($"- {line}");
            }
        }

        Assert.Pass("Computer-use search succeeded; diagnostics captured for review.");
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = new();
        private readonly object _gate = new();
        private bool _disposed;

        public IReadOnlyCollection<string> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, this);

        public void Dispose()
        {
            _disposed = true;
        }

        private void Record(LogLevel level, string category, string message, Exception? exception)
        {
            if (_disposed)
            {
                return;
            }

            var builder = new StringBuilder();
            builder.Append('[')
                .Append(level)
                .Append("] ")
                .Append(category)
                .Append(':')
                .Append(' ')
                .Append(message);

            if (exception is not null)
            {
                builder.Append(" | ")
                    .Append(exception.GetType().Name)
                    .Append(':')
                    .Append(' ')
                    .Append(exception.Message);
            }

            lock (_gate)
            {
                _entries.Add(builder.ToString());
            }
        }

        private sealed class TestLogger : ILogger
        {
            private readonly string _category;
            private readonly TestLoggerProvider _provider;

            public TestLogger(string category, TestLoggerProvider provider)
            {
                _category = category;
                _provider = provider;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (formatter is null)
                {
                    throw new ArgumentNullException(nameof(formatter));
                }

                string message = formatter(state, exception);
                _provider.Record(logLevel, _category, message, exception);
            }
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
