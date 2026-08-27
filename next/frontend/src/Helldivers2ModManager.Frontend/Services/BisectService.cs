using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record BisectCandidate(Guid Id, string Name);

public sealed record BisectRound(int Index, IReadOnlyList<BisectCandidate> Tested, bool? Crashed);

public sealed record BisectSuspect(Guid Id, string Name);

public sealed record BisectSession(
    IReadOnlyList<EnabledStateRecord> OriginalSnapshot,
    IReadOnlyList<ModItem> AllMods,
    IReadOnlyList<BisectCandidate> Candidates,
    IReadOnlyList<BisectRound> Rounds,
    IReadOnlyList<BisectSuspect> Suspects);

public sealed record BisectPreparedRound(
    BisectSession Session,
    IReadOnlyList<BisectCandidate> Tested,
    IReadOnlyList<ModItem> DeployableMods);

public sealed class BisectService(
    EnabledStateRepository enabledStates,
    ModLibraryService library)
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BisectSession? Current { get; private set; }

    public bool HasSession => Current is not null;

    public async Task<BisectPreparedRound> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Current is not null)
            {
                throw new InvalidOperationException("A bisect session is already running.");
            }

            var mods = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
            await library.SaveAsync(mods.Mods, cancellationToken).ConfigureAwait(false);
            var snapshot = await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var enabled = mods.Mods.Where(mod => mod.IsEnabled).ToArray();
            if (enabled.Length < 2)
            {
                throw new InvalidOperationException("Bisect requires at least two enabled mods.");
            }

            Current = new BisectSession(
                snapshot,
                mods.Mods,
                [.. enabled.Select(mod => new BisectCandidate(mod.Id, mod.Name))
                    .OrderBy(candidate => GetSortOrder(snapshot, candidate.Id))],
                [],
                []);
            return await PrepareRoundCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BisectPreparedRound> PrepareRoundAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await PrepareRoundCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BisectSession> ApplyReportAsync(
        IReadOnlyList<BisectCandidate> tested,
        bool crashed,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current ?? throw new InvalidOperationException("No bisect session is running.");
            var testedIds = tested.Select(candidate => candidate.Id).ToHashSet();
            var next = crashed
                ? session.Candidates.Where(candidate => testedIds.Contains(candidate.Id)).ToArray()
                : session.Candidates.Where(candidate => !testedIds.Contains(candidate.Id)).ToArray();
            Current = session with
            {
                Candidates = next,
                Rounds = [.. session.Rounds, new(session.Rounds.Count + 1, tested, crashed)],
            };
            return Current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BisectPreparedRound> PrepareSingleVerificationAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current ?? throw new InvalidOperationException("No bisect session is running.");
            if (session.Candidates.Count != 1)
            {
                throw new InvalidOperationException("Bisect has not converged to one candidate.");
            }

            var candidate = session.Candidates[0];
            var candidateId = new[] { candidate.Id }.ToHashSet();
            var loaded = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
            var mods = loaded.Mods
                .Select(mod =>
                {
                    mod.IsEnabled = candidateId.Contains(mod.Id);
                    return mod;
                })
                .ToArray();
            await library.SaveAsync(mods, cancellationToken).ConfigureAwait(false);
            return new(session, [candidate], [.. mods.Where(mod => mod.IsEnabled)]);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BisectSession> ApplySingleReportAsync(
        bool crashed,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current ?? throw new InvalidOperationException("No bisect session is running.");
            if (session.Candidates.Count != 1)
            {
                throw new InvalidOperationException("Bisect has not converged to one candidate.");
            }

            var candidate = session.Candidates[0];
            var records = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = records.FindIndex(record => record.ModGuid == candidate.Id);
            if (crashed && index >= 0)
            {
                records[index] = records[index] with { Enabled = false };
                await enabledStates.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);
            }

            Current = session with
            {
                Candidates = [],
                Rounds = [.. session.Rounds, new(session.Rounds.Count + 1, [candidate], crashed)],
                Suspects = crashed
                    ? [.. session.Suspects, new BisectSuspect(candidate.Id, candidate.Name)]
                    : session.Suspects,
            };
            return Current;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<BisectSession> RestoreOriginalAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = Current ?? throw new InvalidOperationException("No bisect session is running.");
            await enabledStates.ReplaceAllAsync(session.OriginalSnapshot, cancellationToken).ConfigureAwait(false);
            Current = null;
            return session;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(BisectSession Session, bool SuspectsApplied)> FinishAsync(
        bool disableSuspectsInOriginalGroup,
        CancellationToken cancellationToken = default)
    {
        var session = await RestoreOriginalAsync(cancellationToken).ConfigureAwait(false);
        var applied = false;
        if (disableSuspectsInOriginalGroup && session.Suspects.Count > 0)
        {
            var suspectIds = session.Suspects.Select(suspect => suspect.Id).ToHashSet();
            var records = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false))
                .Select(record => suspectIds.Contains(record.ModGuid)
                    ? record with { Enabled = false }
                    : record)
                .ToArray();
            await enabledStates.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);
            applied = true;
        }

        return (session, applied);
    }

    private async Task<BisectPreparedRound> PrepareRoundCoreAsync(CancellationToken cancellationToken)
    {
        var session = Current ?? throw new InvalidOperationException("No bisect session is running.");
        if (session.Candidates.Count < 2)
        {
            throw new InvalidOperationException("Not enough candidates for another round.");
        }

        var tested = session.Candidates.Take(session.Candidates.Count / 2).ToArray();
        var testedIds = tested.Select(candidate => candidate.Id).ToHashSet();
        var loaded = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var mods = loaded.Mods
            .Select(mod =>
            {
                mod.IsEnabled = testedIds.Contains(mod.Id);
                return mod;
            })
            .ToArray();
        await library.SaveAsync(mods, cancellationToken).ConfigureAwait(false);
        return new(session, tested, [.. mods.Where(mod => mod.IsEnabled)]);
    }

    private static int GetSortOrder(IReadOnlyList<EnabledStateRecord> records, Guid id) =>
        records.FirstOrDefault(record => record.ModGuid == id)?.SortOrder ?? int.MaxValue;
}
