namespace Marconian.ResearchAgent.Streaming;

public interface IConsoleStreamWriter
{
    Task WriteAsync(string message, CancellationToken cancellationToken);
}
