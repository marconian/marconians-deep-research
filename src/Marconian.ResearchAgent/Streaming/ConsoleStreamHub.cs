using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleStreamHub : IAsyncDisposable
{
    private readonly ConsoleStreamingOptions _options;
    private readonly IConsoleStreamSummarizer _summarizer;
    private readonly IConsoleStreamWriter _writer;
    private readonly ILogger<ConsoleStreamHub> _logger;
    private readonly ConcurrentQueue<ThoughtEvent> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private long _pending;

    public ConsoleStreamHub(
        IOptions<ConsoleStreamingOptions> options,
        IConsoleStreamSummarizer? summarizer,
        IConsoleStreamWriter? writer,
        ILogger<ConsoleStreamHub> logger)
        : this(options?.Value ?? new ConsoleStreamingOptions(), summarizer, writer, logger)
    {
    }

    public ConsoleStreamHub(
        ConsoleStreamingOptions options,
        IConsoleStreamSummarizer? summarizer,
        IConsoleStreamWriter? writer,
        ILogger<ConsoleStreamHub> logger)
    {
        _options = options ?? new ConsoleStreamingOptions();
        _summarizer = summarizer ?? new DefaultConsoleStreamSummarizer();
        _writer = writer ?? new ConsoleStreamWriter();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!_options.Enabled)
        {
            _worker = Task.CompletedTask;
            _cts.Cancel();
            return;
        }

        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public bool IsEnabled => _options.Enabled;

    public void Enqueue(ThoughtEvent thoughtEvent)
    {
        ArgumentNullException.ThrowIfNull(thoughtEvent);

        if (!IsEnabled)
        {
            return;
        }

        _queue.Enqueue(thoughtEvent);
        Interlocked.Increment(ref _pending);
        _logger.LogTrace("Queued thought event {@ThoughtEvent}", thoughtEvent);
        _signal.Release();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        var buffer = new List<ThoughtEvent>(_options.BatchSize);
        await ProcessPendingAsync(buffer, cancellationToken, forceFlush: true).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var interval = _options.SummaryInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : _options.SummaryInterval;

        var buffer = new List<ThoughtEvent>(Math.Max(1, _options.BatchSize));

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await ProcessPendingAsync(buffer, token, forceFlush: true).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await ProcessPendingAsync(buffer, CancellationToken.None, forceFlush: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Console stream hub failed while flushing pending events during shutdown.");
            }
        }
    }

    private async Task ProcessPendingAsync(List<ThoughtEvent> buffer, CancellationToken token, bool forceFlush)
    {
        while (_queue.TryDequeue(out ThoughtEvent? thought))
        {
            Interlocked.Decrement(ref _pending);
            buffer.Add(thought);

            if (buffer.Count >= _options.BatchSize && _options.BatchSize > 0)
            {
                await DeliverBatchAsync(buffer, token).ConfigureAwait(false);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0 && (forceFlush || _options.BatchSize <= 0))
        {
            await DeliverBatchAsync(buffer, token).ConfigureAwait(false);
            buffer.Clear();
        }
    }

    private async Task DeliverBatchAsync(List<ThoughtEvent> batch, CancellationToken token)
    {
        if (batch.Count == 0)
        {
            return;
        }

        foreach (ThoughtEvent evt in batch)
        {
            _logger.LogDebug("[ThoughtEvent] {Phase} | {Actor} | {Severity} :: {Detail}", evt.Phase, evt.Actor, evt.Severity, evt.Detail);
        }

        string? summary = null;
        if (_options.UseSummarizer)
        {
            try
            {
                summary = await _summarizer.SummarizeAsync(batch, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Console stream summarizer failed. Using fallback summary.");
            }
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = BuildFallbackSummary(batch);
        }

        await _writer.WriteAsync(summary, token).ConfigureAwait(false);
    }

    private static string BuildFallbackSummary(List<ThoughtEvent> batch)
    {
        var first = batch[0];
        var last = batch[^1];
        var builder = new StringBuilder();
        builder.Append('[')
            .Append(first.Timestamp.ToString("HH:mm:ss"))
            .Append(" ➜ ")
            .Append(last.Timestamp.ToString("HH:mm:ss"))
            .Append(']');
        builder.Append(' ');
        builder.AppendLine("Agent updates:");

        foreach (ThoughtEvent evt in batch)
        {
            builder.Append(" - (")
                .Append(evt.Phase)
                .Append(") ")
                .Append(evt.Actor);

            if (evt.HasCorrelation)
            {
                builder.Append(" [").Append(evt.CorrelationId).Append(']');
            }

            builder.Append(':')
                .Append(' ')
                .AppendLine(evt.Detail);
        }

        return builder.ToString().TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsEnabled)
        {
            _signal.Dispose();
            _cts.Dispose();
            return;
        }

        _cts.Cancel();
        _signal.Release();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Console stream hub worker terminated with errors during disposal.");
        }

        _signal.Dispose();
        _cts.Dispose();
    }
}
