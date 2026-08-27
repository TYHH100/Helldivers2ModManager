using Microsoft.Data.Sqlite;

namespace Helldivers2ModManager.Core.Persistence;

public sealed class ProfileRepository(Database database)
{
    public async Task<ProfileSnapshot> GetOrCreateDefaultAsync(CancellationToken cancellationToken = default)
    {
        var existing = await LoadDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;

        var now = DateTimeOffset.UtcNow;
        var created = new ProfileSnapshot(
            Guid.NewGuid(),
            "Default",
            true,
            now,
            now,
            [],
            []);
        await SaveAsync(created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<ProfileSnapshot?> LoadDefaultAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = await database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM profiles WHERE IsDefault=1 ORDER BY CreatedAtUtc LIMIT 1";
            var idText = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (idText is null || !Guid.TryParse(idText, out var profileId)) return null;
            return await LoadCoreAsync(connection, profileId, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return snapshots;
    }

    public async Task<ProfileSnapshot?> LoadAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync(async connection => await LoadCoreAsync(connection, profileId, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

    public Task SaveAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            await using var upsertProfile = connection.CreateCommand();
            upsertProfile.Transaction = transaction;
            upsertProfile.CommandText = """
                INSERT INTO profiles(Id, Name, IsDefault, CreatedAtUtc, UpdatedAtUtc)
                VALUES($id,$name,$isDefault,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,
                    IsDefault=excluded.IsDefault, UpdatedAtUtc=excluded.UpdatedAtUtc
                """;
            upsertProfile.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
            upsertProfile.Parameters.AddWithValue("$name", snapshot.Name);
            upsertProfile.Parameters.AddWithValue("$isDefault", snapshot.IsDefault ? 1 : 0);
            upsertProfile.Parameters.AddWithValue("$created", snapshot.CreatedAtUtc.ToString("O"));
            upsertProfile.Parameters.AddWithValue("$updated", snapshot.UpdatedAtUtc.ToString("O"));
            await upsertProfile.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteGroups = connection.CreateCommand();
            deleteGroups.Transaction = transaction;
            deleteGroups.CommandText = "DELETE FROM mod_groups WHERE ProfileId=$profileId";
            deleteGroups.Parameters.AddWithValue("$profileId", snapshot.Id.ToString("D"));
            await deleteGroups.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var group in snapshot.Groups)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mod_groups(Id, ProfileId, Name, DisplayIndex, CreatedAtUtc)
                    VALUES($id,$profileId,$name,$displayIndex,$created)
                    """;
                command.Parameters.AddWithValue("$id", group.Id.ToString("D"));
                command.Parameters.AddWithValue("$profileId", snapshot.Id.ToString("D"));
                command.Parameters.AddWithValue("$name", group.Name);
                command.Parameters.AddWithValue("$displayIndex", group.DisplayIndex);
                command.Parameters.AddWithValue("$created", group.CreatedAtUtc.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var deleteStates = connection.CreateCommand();
            deleteStates.Transaction = transaction;
            deleteStates.CommandText = "DELETE FROM mod_states WHERE ProfileId=$profileId";
            deleteStates.Parameters.AddWithValue("$profileId", snapshot.Id.ToString("D"));
            await deleteStates.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var state in snapshot.Mods)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mod_states(ProfileId, ModGuid, Enabled, GroupId, SortOrder, StateJson)
                    VALUES($profileId,$modGuid,$enabled,$groupId,$sortOrder,$stateJson)
                    """;
                command.Parameters.AddWithValue("$profileId", snapshot.Id.ToString("D"));
                command.Parameters.AddWithValue("$modGuid", state.ModGuid.ToString("D"));
                command.Parameters.AddWithValue("$enabled", state.Enabled ? 1 : 0);
                var groupParameter = command.Parameters.Add("$groupId", SqliteType.Text);
                if (state.GroupId.HasValue) groupParameter.Value = state.GroupId.Value.ToString("D");
                else groupParameter.Value = DBNull.Value;
                command.Parameters.AddWithValue("$sortOrder", state.SortOrder);
                command.Parameters.AddWithValue("$stateJson", state.StateJson);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);

    private static async Task<ProfileSnapshot?> LoadCoreAsync(SqliteConnection connection, Guid profileId, CancellationToken cancellationToken)
    {
        await using var profileCommand = connection.CreateCommand();
        profileCommand.CommandText = "SELECT Name,IsDefault,CreatedAtUtc,UpdatedAtUtc FROM profiles WHERE Id=$id";
        profileCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
        var reader = await profileCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var name = reader.GetString(0);
        var isDefault = reader.GetInt64(1) != 0;
        var createdAt = DateTimeOffset.Parse(reader.GetString(2));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(3));
        await reader.DisposeAsync().ConfigureAwait(false);

        var groups = new List<ProfileGroupRecord>();
        await using (var groupCommand = connection.CreateCommand())
        {
            groupCommand.CommandText = "SELECT Id,Name,DisplayIndex,CreatedAtUtc FROM mod_groups WHERE ProfileId=$id ORDER BY DisplayIndex";
            groupCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
            var groupReader = await groupCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await groupReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add(new(
                    Guid.Parse(groupReader.GetString(0)),
                    groupReader.GetString(1),
                    groupReader.GetInt32(2),
                    DateTimeOffset.Parse(groupReader.GetString(3))));
            }
        }

        var states = new List<ProfileModState>();
        await using (var stateCommand = connection.CreateCommand())
        {
            stateCommand.CommandText = "SELECT ModGuid,Enabled,GroupId,SortOrder,StateJson FROM mod_states WHERE ProfileId=$id ORDER BY SortOrder,ModGuid";
            stateCommand.Parameters.AddWithValue("$id", profileId.ToString("D"));
            var stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await stateReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var groupText = stateReader.IsDBNull(2) ? null : stateReader.GetString(2);
                states.Add(new(
                    Guid.Parse(stateReader.GetString(0)),
                    stateReader.GetInt64(1) != 0,
                    groupText is null ? null : Guid.Parse(groupText),
                    stateReader.GetInt32(3),
                    stateReader.GetString(4)));
            }
        }

        return new(profileId, name, isDefault, createdAt, updatedAt, groups, states);
    }
}
