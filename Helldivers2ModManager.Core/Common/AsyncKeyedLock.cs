using System.Collections.Concurrent;

namespace Helldivers2ModManager.Core.Common;

public sealed class AsyncKeyedLock<TKey> where TKey : notnull
{
    private sealed class LockState(SemaphoreSlim semaphore)
    {
        public SemaphoreSlim Semaphore { get; } = semaphore;
        public int ReferenceCount;
    }

    private readonly ConcurrentDictionary<TKey, LockState> _locks = new();

    public async Task<IAsyncDisposable> LockAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        var state = _locks.GetOrAdd(key, static _ => new LockState(new SemaphoreSlim(1, 1)));
        lock (state)
        {
            Interlocked.Increment(ref state.ReferenceCount);
        }

        try
        {
            await state.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            RemoveReference(key, state);
            throw;
        }

        return new Releaser(this, key, state);
    }

    private void RemoveReference(TKey key, LockState state)
    {
        lock (state)
        {
            if (Interlocked.Decrement(ref state.ReferenceCount) == 0)
            {
                ((ICollection<KeyValuePair<TKey, LockState>>)_locks).Remove(new KeyValuePair<TKey, LockState>(key, state));
            }
        }
    }

    private sealed class Releaser(AsyncKeyedLock<TKey> owner, TKey key, LockState state) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            state.Semaphore.Release();
            owner.RemoveReference(key, state);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
