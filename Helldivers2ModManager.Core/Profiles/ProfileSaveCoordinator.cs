using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Profiles;

public sealed class ProfileSaveCoordinator(
    Func<Persistence.ProfileSnapshot, Task> saveAsync,
    ILogger<ProfileSaveCoordinator> logger)
{
    private const int DebounceMilliseconds = 300;
    private readonly Lock sync = new();
    private long nextSequence;
    private SnapshotEnvelope? currentSnapshot;
    private SnapshotEnvelope? pendingSnapshot;
    private CancellationTokenSource? debounceCancellation;
    private Task saveQueueTail = Task.CompletedTask;

    public Persistence.ProfileSnapshot Capture(ProfileCaptureRequest request)
    {
        var sequence = Interlocked.Increment(ref nextSequence);
        var snapshot = ProfileStateService.Capture(sequence, request);
        lock (sync)
        {
            if (currentSnapshot is null || sequence >= currentSnapshot.Value.Sequence)
            {
                currentSnapshot = new(sequence, snapshot);
            }
        }

        return snapshot;
    }

    public IReadOnlyList<Guid>? GetCurrentOrder()
    {
        lock (sync)
        {
            return currentSnapshot?.Snapshot.Mods
                .OrderBy(static state => state.SortOrder)
                .Select(static state => state.ModGuid)
                .ToArray();
        }
    }

    public void RequestSave(ProfileCaptureRequest request)
    {
        var (sequence, snapshot) = CaptureWithSequence(request);
        CancellationTokenSource cancellation;
        lock (sync)
        {
            UpdateCurrentAndPendingLocked(sequence, snapshot);
            debounceCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            debounceCancellation = cancellation;
        }

        _ = RunDebouncedSaveAsync(cancellation);
    }

    public Task SaveNowAsync(ProfileCaptureRequest request)
    {
        var (sequence, snapshot) = CaptureWithSequence(request);
        lock (sync)
        {
            UpdateCurrentAndPendingLocked(sequence, snapshot);
            debounceCancellation?.Cancel();
            debounceCancellation = null;
            if (pendingSnapshot is null)
            {
                return saveQueueTail;
            }

            var toSave = pendingSnapshot.Value;
            pendingSnapshot = null;
            return QueueSaveLocked(toSave);
        }
    }

    public Task FlushAsync()
    {
        lock (sync)
        {
            debounceCancellation?.Cancel();
            debounceCancellation = null;
            if (pendingSnapshot is not null)
            {
                QueueSaveLocked(pendingSnapshot.Value);
                pendingSnapshot = null;
            }

            return saveQueueTail;
        }
    }

    private (long Sequence, Persistence.ProfileSnapshot Snapshot) CaptureWithSequence(ProfileCaptureRequest request)
    {
        var sequence = Interlocked.Increment(ref nextSequence);
        return (sequence, ProfileStateService.Capture(sequence, request));
    }

    private void UpdateCurrentAndPendingLocked(long sequence, Persistence.ProfileSnapshot snapshot)
    {
        if (currentSnapshot is null || sequence >= currentSnapshot.Value.Sequence)
        {
            currentSnapshot = new(sequence, snapshot);
        }

        if (pendingSnapshot is null || sequence >= pendingSnapshot.Value.Sequence)
        {
            pendingSnapshot = new(sequence, snapshot);
        }
    }

    private async Task RunDebouncedSaveAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(DebounceMilliseconds), cancellation.Token).ConfigureAwait(false);
            Task saveTask;
            lock (sync)
            {
                if (!ReferenceEquals(debounceCancellation, cancellation))
                {
                    return;
                }

                debounceCancellation = null;
                var pending = pendingSnapshot;
                pendingSnapshot = null;
                saveTask = pending is null ? saveQueueTail : QueueSaveLocked(pending.Value);
            }

            await saveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Automatic profile save failed");
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private Task QueueSaveLocked(SnapshotEnvelope envelope)
    {
        saveQueueTail = PersistAfterPreviousAsync(saveQueueTail, envelope.Snapshot);
        return saveQueueTail;
    }

    private async Task PersistAfterPreviousAsync(Task previousSave, Persistence.ProfileSnapshot snapshot)
    {
        try
        {
            await previousSave.ConfigureAwait(false);
        }
        catch
        {
        }

        await saveAsync(snapshot).ConfigureAwait(false);
    }

    private readonly record struct SnapshotEnvelope(long Sequence, Persistence.ProfileSnapshot Snapshot);
}
