namespace Helldivers2ModManager.Core.Common;

public sealed class ConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public ConcurrencyLimiter(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
