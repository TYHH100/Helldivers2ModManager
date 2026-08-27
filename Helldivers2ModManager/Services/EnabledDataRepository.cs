using System.Text.Json;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Maps the UI's legacy enabled-state struct to Core default-profile states.
/// Storage-path parameters are retained only while named-group migration finishes.
/// </summary>
internal sealed class EnabledDataRepository(EnabledStateRepository repository)
{
    public Task SaveAllAsync(string storageDirectory, IReadOnlyList<EnabledData> enabledDataList) =>
        SaveAllAsync(enabledDataList);

    public Task SaveAllAsync(IReadOnlyList<EnabledData> enabledDataList, CancellationToken cancellationToken = default) =>
        repository.ReplaceAllAsync(enabledDataList.Select(static (data, index) => new EnabledStateRecord(
            data.Guid,
            data.Enabled,
            index,
            JsonSerializer.Serialize(data))), cancellationToken);

    public async Task<List<EnabledData>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var records = await repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return records.Select(static record => JsonSerializer.Deserialize<EnabledData>(record.StateJson)).ToList();
    }

    public Task DeleteByGuidsAsync(IEnumerable<Guid> guids, CancellationToken cancellationToken = default) =>
        repository.DeleteByGuidsAsync(guids, cancellationToken);

    public async Task<bool> HasDataAsync(CancellationToken cancellationToken = default)
    {
        var records = await repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return records.Count > 0;
    }

    public async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        var records = await repository.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return records.Count;
    }
}
