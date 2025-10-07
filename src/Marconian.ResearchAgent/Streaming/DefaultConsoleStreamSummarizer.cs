using System.Text;

namespace Marconian.ResearchAgent.Streaming;

public sealed class DefaultConsoleStreamSummarizer : IConsoleStreamSummarizer
{
    public Task<string?> SummarizeAsync(IReadOnlyList<ThoughtEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        var builder = new StringBuilder();
        var windowStart = events[0].Timestamp;
        var windowEnd = events[^1].Timestamp;

        builder.Append('[')
            .Append(windowStart.ToString("HH:mm:ss"))
            .Append(" ➜ ")
            .Append(windowEnd.ToString("HH:mm:ss"))
            .Append("] ");

        builder.Append("Updates:");
        builder.AppendLine();

        foreach (ThoughtEvent evt in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.Append("  • (")
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

        return Task.FromResult<string?>(builder.ToString().TrimEnd());
    }
}
