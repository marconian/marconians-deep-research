namespace Marconian.ResearchAgent.Streaming;

public sealed class ConsoleStreamWriter : IConsoleStreamWriter
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);

    public async Task WriteAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await ConsoleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Console.Out.WriteLine(message);
            Console.Out.Flush();
        }
        finally
        {
            ConsoleLock.Release();
        }
    }
}
