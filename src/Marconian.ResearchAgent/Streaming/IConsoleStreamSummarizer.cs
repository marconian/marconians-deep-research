namespace Marconian.ResearchAgent.Streaming;

public interface IConsoleStreamSummarizer
{
    Task<string?> SummarizeAsync(IReadOnlyList<ThoughtEvent> events, CancellationToken cancellationToken);
}
