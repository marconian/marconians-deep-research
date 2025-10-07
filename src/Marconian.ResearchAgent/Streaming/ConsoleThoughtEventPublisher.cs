using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleThoughtEventPublisher : IThoughtEventPublisher
{
    private readonly ConsoleStreamHub _hub;
    private readonly ILogger<ConsoleThoughtEventPublisher> _logger;

    public ConsoleThoughtEventPublisher(ConsoleStreamHub hub, ILogger<ConsoleThoughtEventPublisher>? logger = null)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _logger = logger ?? NullLogger<ConsoleThoughtEventPublisher>.Instance;
    }

    public bool IsEnabled => _hub.IsEnabled;

    public void Publish(string phase, string actor, string detail, ThoughtSeverity severity = ThoughtSeverity.Info, Guid? correlationId = null, DateTimeOffset? timestampUtc = null)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return;
        }

        DateTimeOffset timestamp = timestampUtc ?? DateTimeOffset.UtcNow;
        var thoughtEvent = new ThoughtEvent(phase, actor, detail, timestamp, correlationId, severity);
        _logger.LogTrace("Emitting thought event {@ThoughtEvent}", thoughtEvent);
        _hub.Enqueue(thoughtEvent);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _hub.FlushAsync(cancellationToken);
}
