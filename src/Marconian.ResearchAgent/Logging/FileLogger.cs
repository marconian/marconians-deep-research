using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Marconian.ResearchAgent.Logging;

internal sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string categoryName, FileLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider.MinLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        string line = $"{DateTime.UtcNow:O} {logLevel} {_categoryName} :: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        _provider.Enqueue(line);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly Timer _flushTimer;
    private readonly ConcurrentQueue<string> _queue = new();
    private volatile bool _disposed;

    public FileLoggerProvider(string filePath, LogLevel minLevel, TimeSpan? flushInterval = null)
    {
        _filePath = filePath;
        MinLevel = minLevel;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _flushTimer = new Timer(_ => FlushInternal(), null, flushInterval ?? TimeSpan.FromSeconds(2), flushInterval ?? TimeSpan.FromSeconds(2));
    }

    internal LogLevel MinLevel { get; }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        _disposed = true;
        _flushTimer.Dispose();
        FlushInternal();
    }

    internal void Enqueue(string line)
    {
        _queue.Enqueue(line);
    }

    private void FlushInternal()
    {
        if (_disposed || _queue.IsEmpty)
        {
            return;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream);
            while (_queue.TryDequeue(out string? line))
            {
                writer.WriteLine(line);
            }
        }
        catch
        {
            // swallow diagnostics failures
        }
    }
}

internal static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, string? path, LogLevel minLevel)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return builder;
        }

        try
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string fileName = Path.GetFileNameWithoutExtension(path);
            string extension = Path.GetExtension(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "marconian";
            }
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".log";
            }

            string stamped = Path.Combine(directory, $"{fileName}-{DateTime.UtcNow:yyyyMMdd_HHmmss}{extension}");
            builder.AddProvider(new FileLoggerProvider(stamped, minLevel));
        }
        catch
        {
            builder.AddProvider(new FileLoggerProvider(path!, minLevel));
        }

        return builder;
    }
}
