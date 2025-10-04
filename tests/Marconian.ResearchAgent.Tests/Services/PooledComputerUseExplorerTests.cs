using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Marconian.ResearchAgent.Models.Tools;
using Marconian.ResearchAgent.Services.ComputerUse;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Services;

[TestFixture]
public sealed class PooledComputerUseExplorerTests
{
    [Test]
    public async Task ExploreAsync_ShouldRespectMaxParallelism()
    {
        int activeCount = 0;
        int maxActive = 0;

        await using var pool = new PooledComputerUseExplorer(
            maxConcurrentSessions: 2,
            explorerFactory: () => new TestExplorer(
                async () =>
                {
                    int current = Interlocked.Increment(ref activeCount);
                    UpdateMax(ref maxActive, current);
                    await Task.CompletedTask.ConfigureAwait(false);
                },
                () => Interlocked.Decrement(ref activeCount),
                TimeSpan.FromMilliseconds(75)),
            NullLogger<PooledComputerUseExplorer>.Instance);

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => pool.ExploreAsync("https://example.com", "objective", CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.That(maxActive, Is.EqualTo(2), "Parallel explorer usage should not exceed configured capacity.");
    }

    [Test]
    public async Task ExploreAsync_ShouldReuseExplorerInstances()
    {
        int created = 0;

        await using var pool = new PooledComputerUseExplorer(
            maxConcurrentSessions: 1,
            explorerFactory: () =>
            {
                Interlocked.Increment(ref created);
                return new TestExplorer(
                    () => ValueTask.CompletedTask,
                    () => { },
                    TimeSpan.FromMilliseconds(25));
            },
            NullLogger<PooledComputerUseExplorer>.Instance);

        for (int i = 0; i < 4; i++)
        {
            await pool.ExploreAsync("https://example.com", null, CancellationToken.None).ConfigureAwait(false);
        }

        Assert.That(created, Is.EqualTo(1), "Single-slot pool should reuse the same explorer instance across calls.");
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < candidate &&
               Interlocked.CompareExchange(ref target, candidate, current) != current)
        {
        }
    }

    private sealed class TestExplorer : IComputerUseExplorer, IAsyncDisposable
    {
        private readonly Func<ValueTask> _onStart;
        private readonly Action _onFinish;
        private readonly TimeSpan _delay;

        public TestExplorer(Func<ValueTask> onStart, Action onFinish, TimeSpan delay)
        {
            _onStart = onStart ?? throw new ArgumentNullException(nameof(onStart));
            _onFinish = onFinish ?? throw new ArgumentNullException(nameof(onFinish));
            _delay = delay;
        }

        public async Task<ComputerUseExplorationResult> ExploreAsync(string url, string? objective = null, CancellationToken cancellationToken = default)
        {
            await _onStart().ConfigureAwait(false);
            try
            {
                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
                return new ComputerUseExplorationResult(
                    url,
                    url,
                    "Title",
                    "Summary",
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<FlaggedResource>());
            }
            finally
            {
                _onFinish();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
