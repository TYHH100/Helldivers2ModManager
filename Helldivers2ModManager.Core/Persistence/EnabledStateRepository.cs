using Microsoft.Data.Sqlite;

namespace Helldivers2ModManager.Core.Persistence;

public sealed record EnabledStateRecord(
    Guid ModGuid,
    bool Enabled,
    int SortOrder,
    string StateJson);

/// <summary>
/// Default-profile enabled states. Non-null <code>GroupId</code> rows in
/// mod_states belong to named group snapshots and are intentionally excluded.
/// </summary>
public sealed class EnabledStateRepository(Database database, ProfileRepository profiles)
{
    public async Task<IReadOnlyList<EnabledStateRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
        return await database.ExecuteAsync(async connection =>
        {
            var records = new List<EnabledStateRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ModGuid,Enabled,SortOrder,StateJson FROM mod_states
                WHERE ProfileId=$profileId AND GroupId IS NULL
                ORDER BY SortOrder,ModGuid
                """;
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(new(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt64(1) != 0,
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }

            return records;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task ReplaceAllAsync(IEnumerable<EnabledStateRecord> records, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM mod_states WHERE ProfileId=$profileId AND GroupId IS NULL";
                clear.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                foreach (var record in records.OrderBy(static item => item.SortOrder))
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO mod_states(ProfileId,ModGuid,Enabled,GroupId,SortOrder,StateJson)
                        VALUES($profileId,$modGuid,$enabled,NULL,$sortOrder,$stateJson)
                        """;
                    command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                    command.Parameters.AddWithValue("$modGuid", record.ModGuid.ToString("D"));
                    command.Parameters.AddWithValue("$enabled", record.Enabled ? 1 : 0);
                    command.Parameters.AddWithValue("$sortOrder", record.SortOrder);
                    command.Parameters.AddWithValue("$stateJson", record.StateJson);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);

    public Task DeleteByGuidsAsync(IEnumerable<Guid> modGuids, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            var profileId = await GetDefaultProfileIdAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var modGuid in modGuids)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM mod_states WHERE ProfileId=$profileId AND ModGuid=$modGuid AND GroupId IS NULL";
                    command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                    command.Parameters.AddWithValue("$modGuid", modGuid.ToString("D"));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, cancellationToken);

    private async Task<Guid> GetDefaultProfileIdAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false);
        return profile.Id;
    }
}
