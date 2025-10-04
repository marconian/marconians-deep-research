using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Models.Tools;
using Microsoft.Extensions.Logging;

namespace Marconian.ResearchAgent.Services.ComputerUse;

public sealed class PooledComputerUseExplorer : IComputerUseExplorer, IAsyncDisposable
{
    private readonly Func<IComputerUseExplorer> _explorerFactory;
    private readonly ConcurrentQueue<IComputerUseExplorer> _availableExplorers = new();
    private readonly List<IComputerUseExplorer> _allExplorers = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly ILogger<PooledComputerUseExplorer> _logger;
    private bool _disposed;

    public PooledComputerUseExplorer(
        int maxConcurrentSessions,
        Func<IComputerUseExplorer> explorerFactory,
        ILogger<PooledComputerUseExplorer>? logger = null)
    {
        if (maxConcurrentSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentSessions), maxConcurrentSessions, "Maximum concurrent sessions must be at least 1.");
        }

    _explorerFactory = explorerFactory ?? throw new ArgumentNullException(nameof(explorerFactory));
    _capacity = maxConcurrentSessions;
    _concurrencySemaphore = new SemaphoreSlim(maxConcurrentSessions, maxConcurrentSessions);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PooledComputerUseExplorer>.Instance;
    }

    public async Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        IComputerUseExplorer? explorer = null;
        try
        {
            explorer = RentExplorer();
            return await explorer.ExploreAsync(url, objective, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (explorer is not null)
            {
                ReturnExplorer(explorer);
            }

            _concurrencySemaphore.Release();
        }
    }

    private IComputerUseExplorer RentExplorer()
    {
        if (_availableExplorers.TryDequeue(out var explorer))
        {
            return explorer;
        }

        lock (_gate)
        {
            if (_availableExplorers.TryDequeue(out explorer))
            {
                return explorer;
            }

            explorer = _explorerFactory();
            _allExplorers.Add(explorer);
            _logger.LogDebug("Created new computer-use explorer instance. Total instances: {Count}.", _allExplorers.Count);
            return explorer;
        }
    }

    private void ReturnExplorer(IComputerUseExplorer explorer)
    {
        if (_disposed)
        {
            _ = DisposeExplorerAsync(explorer);
            return;
        }

        _availableExplorers.Enqueue(explorer);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        List<IComputerUseExplorer> explorers;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            explorers = new List<IComputerUseExplorer>(_allExplorers);
            _allExplorers.Clear();
        }

        while (_availableExplorers.TryDequeue(out _))
        {
        }

        for (int i = 0; i < _capacity; i++)
        {
            await _concurrencySemaphore.WaitAsync().ConfigureAwait(false);
        }

        _concurrencySemaphore.Dispose();

        foreach (IComputerUseExplorer explorer in explorers)
        {
            await DisposeExplorerAsync(explorer).ConfigureAwait(false);
        }
    }

    private static async ValueTask DisposeExplorerAsync(IComputerUseExplorer explorer)
    {
        switch (explorer)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PooledComputerUseExplorer));
        }
    }
}
