using System.Collections.Concurrent;

namespace TechBench.Services;

public sealed class PostingExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        int workEntryId,
        string destination,
        CancellationToken cancellationToken = default)
    {
        var key = $"{workEntryId}:{destination.Trim()}";
        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        return new Lease(this, key, gate);
    }

    private void Release(string key, SemaphoreSlim gate)
    {
        gate.Release();
        if (gate.CurrentCount == 1)
        {
            _locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
        }
    }

    private sealed class Lease(
        PostingExecutionCoordinator owner,
        string key,
        SemaphoreSlim gate) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(key, gate);
            }

            return ValueTask.CompletedTask;
        }
    }
}
