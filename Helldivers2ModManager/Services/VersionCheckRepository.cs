using System.Globalization;
using System.Text.Json;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Version-check cache facade that preserves the UI tuple model while storage
/// moves to the unified Core database.
/// </summary>
internal sealed class VersionCheckRepository(VersionResultRepository repository)
{
    private readonly record struct GameVersionEntry(uint GameVersion);

    public async Task<Dictionary<Guid, (ModVersionStatus Status, uint GameVersion, DateTime LastChecked, DateTime ModLastWriteTimeUtc)>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<Guid, (ModVersionStatus, uint, DateTime, DateTime)>(records.Count);
        foreach (var record in records)
        {
            var entry = JsonSerializer.Deserialize<GameVersionEntry>(record.ResultJson);
            results[record.ModGuid] = (
                (ModVersionStatus)record.Status,
                entry.GameVersion,
                record.CheckedAtUtc.UtcDateTime,
                record.ModLastWriteTimeUtc?.UtcDateTime ?? DateTime.MinValue);
        }

        return results;
    }

    public Task SaveAllAsync(
        Dictionary<Guid, (ModVersionStatus Status, uint GameVersion, DateTime LastChecked, DateTime ModLastWriteTimeUtc)> results,
        CancellationToken cancellationToken = default)
    {
        return repository.SaveAllAsync(results.Select(result => new VersionResultRecord(
            result.Key,
            (int)result.Value.Status,
            JsonSerializer.Serialize(new GameVersionEntry(result.Value.GameVersion)),
            new DateTimeOffset(DateTime.SpecifyKind(result.Value.LastChecked, DateTimeKind.Utc), TimeSpan.Zero),
            new DateTimeOffset(DateTime.SpecifyKind(result.Value.ModLastWriteTimeUtc, DateTimeKind.Utc), TimeSpan.Zero))), cancellationToken);
    }

    public Task DeleteByGuidAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(modGuid, cancellationToken);

    public async Task<DateTime> GetGameExeLastWriteTimeAsync(CancellationToken cancellationToken = default)
    {
        var value = await repository.GetGameExeLastWriteTimeAsync(cancellationToken).ConfigureAwait(false);
        return value?.UtcDateTime ?? DateTime.MinValue;
    }

    public Task UpdateGameExeLastWriteTimeAsync(DateTime lastWriteTimeUtc, CancellationToken cancellationToken = default) =>
        repository.SetGameExeLastWriteTimeAsync(
            new DateTimeOffset(DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc), TimeSpan.Zero),
            cancellationToken);
}
