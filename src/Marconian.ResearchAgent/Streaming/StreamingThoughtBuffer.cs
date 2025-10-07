using System.Text;

namespace Marconian.ResearchAgent.Streaming;

internal sealed class StreamingThoughtBuffer : IDisposable, IAsyncDisposable
{
    private readonly Action<string> _flushAction;
    private readonly int _flushThreshold;
    private readonly int _maxDetailLength;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public StreamingThoughtBuffer(Action<string> flushAction, int flushThreshold = 120, int maxDetailLength = 280)
    {
        _flushAction = flushAction ?? throw new ArgumentNullException(nameof(flushAction));
        _flushThreshold = Math.Max(1, flushThreshold);
        _maxDetailLength = Math.Max(32, maxDetailLength);
    }

    public ValueTask AppendAsync(string chunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _buffer.Append(chunk);
            if (_buffer.Length >= _flushThreshold || chunk.IndexOfAny(['\n', '\r']) >= 0)
            {
                FlushLocked();
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        string detail = _buffer.ToString().Trim();
        _buffer.Clear();
        if (detail.Length == 0)
        {
            return;
        }

        if (detail.Length > _maxDetailLength)
        {
            detail = detail[.._maxDetailLength].TrimEnd() + "…";
        }

        _flushAction(detail);
    }

    public void Dispose() => Flush();

    public ValueTask DisposeAsync()
    {
        Flush();
        return ValueTask.CompletedTask;
    }
}
