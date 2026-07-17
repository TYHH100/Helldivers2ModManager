namespace Helldivers2ModManager.Extensions;

/// <summary>
/// Explicitly observes infrastructure and UI-lifecycle tasks that cannot be awaited by
/// their synchronous callback contract. User-visible operations still use
/// IBackgroundTaskRunner.
/// </summary>
internal static class TaskObservationExtensions
{
    public static void Observe(
        this Task task,
        Action<Exception> onFault,
        Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(onFault);

        var awaiter = task.ConfigureAwait(false).GetAwaiter();
        if (awaiter.IsCompleted)
        {
            Complete();
            return;
        }

        awaiter.OnCompleted(Complete);
        return;

        void Complete()
        {
            try
            {
                awaiter.GetResult();
            }
            catch (OperationCanceledException)
            {
                // Cancellation is an expected terminal state for supervised work.
            }
            catch (Exception exception)
            {
                onFault(exception);
            }
            finally
            {
                onCompleted?.Invoke();
            }
        }
    }
}
