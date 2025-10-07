using System;
using System.Threading;
using System.Threading.Tasks;

namespace Marconian.ResearchAgent.Streaming;

public sealed class NullThoughtEventPublisher : IThoughtEventPublisher
{
    public static readonly NullThoughtEventPublisher Instance = new();

    private NullThoughtEventPublisher()
    {
    }

    public bool IsEnabled => false;

    public void Publish(string phase, string actor, string detail, ThoughtSeverity severity = ThoughtSeverity.Info, Guid? correlationId = null, DateTimeOffset? timestampUtc = null)
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
