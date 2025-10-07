using System;
using System.Threading;
using System.Threading.Tasks;

namespace Marconian.ResearchAgent.Streaming;

public interface IThoughtEventPublisher
{
    bool IsEnabled { get; }

    void Publish(string phase, string actor, string detail, ThoughtSeverity severity = ThoughtSeverity.Info, Guid? correlationId = null, DateTimeOffset? timestampUtc = null);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
