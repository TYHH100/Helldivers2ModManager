using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Helldivers2ModManager.Core.Persistence;

public interface IFileHashRepository
{
    Task<IReadOnlyList<FileHashRecord>> LoadForModAsync(Guid modGuid, CancellationToken cancellationToken = default);

    Task ReplaceForModAsync(Guid modGuid, IReadOnlyList<FileHashRecord> records, CancellationToken cancellationToken = default);

    Task DeleteForModAsync(Guid modGuid, CancellationToken cancellationToken = default);
}

public sealed class FileHashRepository(Database database) : IFileHashRepository
{
    public Task ReplaceForModAsync(Guid modGuid, IReadOnlyList<FileHashRecord> records, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM file_hashes WHERE ModGuid=$modGuid";
            delete.Parameters.AddWithValue("$modGuid", modGuid.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in records)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO file_hashes(ModGuid, FilePath, FileHash, FileSize, LastModifiedUtc)
                    VALUES($modGuid,$filePath,$hash,$size,$modified)
                    """;
                command.Parameters.AddWithValue("$modGuid", record.ModGuid.ToString("D"));
                command.Parameters.AddWithValue("$filePath", record.FilePath);
                command.Parameters.AddWithValue("$hash", record.FileHash);
                command.Parameters.AddWithValue("$size", record.FileSize);
                command.Parameters.AddWithValue("$modified", record.LastModifiedUtc.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<FileHashRecord>> LoadForModAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync(async connection =>
        {
            var records = new List<FileHashRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT FilePath,FileHash,FileSize,LastModifiedUtc FROM file_hashes WHERE ModGuid=$modGuid ORDER BY FilePath";
            command.Parameters.AddWithValue("$modGuid", modGuid.ToString("D"));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(new(modGuid, reader.GetString(0), reader.GetString(1), reader.GetInt64(2), DateTimeOffset.Parse(reader.GetString(3))));
            }

            return records;
        }, cancellationToken).ConfigureAwait(false);

    public Task DeleteForModAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM file_hashes WHERE ModGuid=$modGuid";
            command.Parameters.AddWithValue("$modGuid", modGuid.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}

public sealed class GroupRepository(Database database)
{
    public async Task<IReadOnlyList<ProfileGroupRecord>> LoadForProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync(async connection =>
        {
            var groups = new List<ProfileGroupRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id,Name,DisplayIndex,CreatedAtUtc FROM mod_groups WHERE ProfileId=$profileId ORDER BY DisplayIndex";
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add(new(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }

            return groups;
        }, cancellationToken).ConfigureAwait(false);

    public Task SaveAsync(Guid profileId, ProfileGroupRecord group, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mod_groups(Id,ProfileId,Name,DisplayIndex,CreatedAtUtc)
                VALUES($id,$profileId,$name,$display,$created)
                ON CONFLICT(Id, ProfileId) DO UPDATE SET
                    Name=excluded.Name, DisplayIndex=excluded.DisplayIndex
                """;
            command.Parameters.AddWithValue("$id", group.Id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$name", group.Name);
            command.Parameters.AddWithValue("$display", group.DisplayIndex);
            command.Parameters.AddWithValue("$created", group.CreatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task ReplaceForProfileAsync(
        Guid profileId,
        IEnumerable<ProfileGroupRecord> groups,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            var desiredGroups = groups.ToArray();
            var desiredIds = desiredGroups.Select(static group => group.Id.ToString("D")).ToHashSet();

            await using var loadCommand = connection.CreateCommand();
            loadCommand.Transaction = transaction;
            loadCommand.CommandText = "SELECT Id FROM mod_groups WHERE ProfileId=$profileId";
            loadCommand.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            var existingIds = new HashSet<string>();
            using (var reader = await loadCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existingIds.Add(reader.GetString(0));
                }
            }

            foreach (var group in desiredGroups)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO mod_groups(Id,ProfileId,Name,DisplayIndex,CreatedAtUtc)
                    VALUES($id,$profileId,$name,$display,$created)
                    ON CONFLICT(Id, ProfileId) DO UPDATE SET
                        Name=excluded.Name, DisplayIndex=excluded.DisplayIndex
                    """;
                upsert.Parameters.AddWithValue("$id", group.Id.ToString("D"));
                upsert.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                upsert.Parameters.AddWithValue("$name", group.Name);
                upsert.Parameters.AddWithValue("$display", group.DisplayIndex);
                upsert.Parameters.AddWithValue("$created", group.CreatedAtUtc.ToString("O"));
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var existingId in existingIds.Where(id => !desiredIds.Contains(id)))
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mod_groups WHERE ProfileId=$profileId AND Id=$id";
                delete.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                delete.Parameters.AddWithValue("$id", existingId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);

    public Task DeleteAsync(Guid profileId, Guid groupId, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            await using var clearCommand = connection.CreateCommand();
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = "UPDATE mod_states SET GroupId=NULL WHERE ProfileId=$profileId AND GroupId=$groupId";
            clearCommand.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            clearCommand.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            await clearCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var memberDelete = connection.CreateCommand();
            memberDelete.Transaction = transaction;
            memberDelete.CommandText = "DELETE FROM mod_group_members WHERE ProfileId=$profileId AND GroupId=$groupId";
            memberDelete.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            memberDelete.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            await memberDelete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM mod_groups WHERE ProfileId=$profileId AND Id=$groupId";
            deleteCommand.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            deleteCommand.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<EnabledStateRecord>> LoadMembersAsync(
        Guid profileId,
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var records = await database.ExecuteAsync(async connection =>
        {
            var items = new List<EnabledStateRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ModGuid,Enabled,SortOrder,StateJson FROM mod_group_members
                WHERE ProfileId=$profileId AND GroupId=$groupId
                ORDER BY SortOrder,ModGuid
                """;
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(new(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetInt64(1) != 0,
                    reader.GetInt32(2),
                    reader.GetString(3)));
            }

            return items;
        }, cancellationToken).ConfigureAwait(false);
        return records;
    }

    public async Task<IReadOnlyList<Guid>> LoadMemberIdsAsync(
        Guid profileId,
        Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var records = await database.ExecuteAsync(async connection =>
        {
            var items = new List<Guid>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ModGuid FROM mod_group_members
                WHERE ProfileId=$profileId AND GroupId=$groupId
                ORDER BY SortOrder,ModGuid
                """;
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(Guid.Parse(reader.GetString(0)));
            }

            return items;
        }, cancellationToken).ConfigureAwait(false);
        return records;
    }

    public Task ReplaceMembersAsync(
        Guid profileId,
        Guid groupId,
        IReadOnlyList<EnabledStateRecord> records,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM mod_group_members WHERE ProfileId=$profileId AND GroupId=$groupId";
            delete.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            delete.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in records.OrderBy(static item => item.SortOrder))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mod_group_members(ProfileId,GroupId,ModGuid,Enabled,SortOrder,StateJson)
                    VALUES($profileId,$groupId,$modGuid,$enabled,$sortOrder,$stateJson)
                    """;
                command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                command.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
                command.Parameters.AddWithValue("$modGuid", record.ModGuid.ToString("D"));
                command.Parameters.AddWithValue("$enabled", record.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$sortOrder", record.SortOrder);
                command.Parameters.AddWithValue("$stateJson", record.StateJson);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);

    public Task DeleteMembersForModsAsync(Guid profileId, IEnumerable<Guid> modGuids, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var modGuid in modGuids)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM mod_group_members WHERE ProfileId=$profileId AND ModGuid=$modGuid";
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

    public Task SaveStatesAsync(
        Guid profileId,
        Guid groupId,
        IReadOnlyList<ProfileModState> states,
        CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            foreach (var state in states.OrderBy(static item => item.SortOrder))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mod_states(ProfileId,ModGuid,Enabled,GroupId,SortOrder,StateJson)
                    VALUES($profileId,$modGuid,$enabled,$groupId,$sortOrder,$stateJson)
                    ON CONFLICT(ProfileId, ModGuid) DO UPDATE SET
                        Enabled=excluded.Enabled, GroupId=excluded.GroupId,
                        SortOrder=excluded.SortOrder, StateJson=excluded.StateJson
                    """;
                command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
                command.Parameters.AddWithValue("$modGuid", state.ModGuid.ToString("D"));
                command.Parameters.AddWithValue("$enabled", state.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$groupId", groupId.ToString("D"));
                command.Parameters.AddWithValue("$sortOrder", state.SortOrder);
                command.Parameters.AddWithValue("$stateJson", state.StateJson);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);
}

public sealed class VersionResultRepository(Database database)
{
    public Task SaveAsync(VersionResultRecord record, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO version_results(ModGuid, Status, ResultJson, CheckedAtUtc, ModLastWriteTimeUtc)
                VALUES($guid,$status,$json,$checked,$modified)
                ON CONFLICT(ModGuid) DO UPDATE SET Status=excluded.Status,
                    ResultJson=excluded.ResultJson, CheckedAtUtc=excluded.CheckedAtUtc,
                    ModLastWriteTimeUtc=excluded.ModLastWriteTimeUtc
                """;
            AddParameters(command, record);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<IReadOnlyList<VersionResultRecord>> LoadAllAsync(CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync(async connection =>
        {
            var records = new List<VersionResultRecord>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ModGuid,Status,ResultJson,CheckedAtUtc,ModLastWriteTimeUtc FROM version_results ORDER BY ModGuid";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(ReadRecord(reader));
            return records;
            }, cancellationToken).ConfigureAwait(false);

    public Task SaveAllAsync(IEnumerable<VersionResultRecord> records, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(connection => Database.ExecuteInTransactionAsync(connection, async transaction =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO version_results(ModGuid, Status, ResultJson, CheckedAtUtc, ModLastWriteTimeUtc)
                VALUES($guid,$status,$json,$checked,$modified)
                ON CONFLICT(ModGuid) DO UPDATE SET Status=excluded.Status,
                    ResultJson=excluded.ResultJson, CheckedAtUtc=excluded.CheckedAtUtc,
                    ModLastWriteTimeUtc=excluded.ModLastWriteTimeUtc
                """;
            foreach (var record in records)
            {
                AddParameters(command, record);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken), cancellationToken);

    public Task DeleteAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM version_results WHERE ModGuid=$guid";
            command.Parameters.AddWithValue("$guid", modGuid.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<DateTimeOffset?> GetGameExeLastWriteTimeAsync(CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync<DateTimeOffset?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ExeLastWriteTimeUtc FROM game_check_tracker WHERE Id=1";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return string.IsNullOrWhiteSpace(value)
                ? null
                : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }, cancellationToken).ConfigureAwait(false);

    public Task SetGameExeLastWriteTimeAsync(DateTimeOffset lastWriteTimeUtc, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO game_check_tracker(Id, ExeLastWriteTimeUtc) VALUES(1,$time)
                ON CONFLICT(Id) DO UPDATE SET ExeLastWriteTimeUtc=excluded.ExeLastWriteTimeUtc
                """;
            command.Parameters.AddWithValue("$time", lastWriteTimeUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static void AddParameters(SqliteCommand command, VersionResultRecord record)
    {
        command.Parameters.AddWithValue("$guid", record.ModGuid.ToString("D"));
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$json", record.ResultJson);
        command.Parameters.AddWithValue("$checked", record.CheckedAtUtc.ToString("O"));
        var modified = command.Parameters.Add("$modified", SqliteType.Text);
        modified.Value = record.ModLastWriteTimeUtc is null ? DBNull.Value : record.ModLastWriteTimeUtc.Value.ToString("O");
    }

    private static VersionResultRecord ReadRecord(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetInt32(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)));
}

public sealed class JsonCacheRepository(Database database)
{
    public async Task<string?> GetAsync(string category, string key, CancellationToken cancellationToken = default) =>
        await database.ExecuteAsync<string?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT ResultJson FROM json_cache WHERE Category=$category AND CacheKey=$key";
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }, cancellationToken).ConfigureAwait(false);

    public Task SetAsync(string category, string key, string resultJson, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO json_cache(Category, CacheKey, ResultJson, UpdatedAtUtc)
                VALUES($category,$key,$json,$updated)
                ON CONFLICT(Category, CacheKey) DO UPDATE SET
                    ResultJson=excluded.ResultJson, UpdatedAtUtc=excluded.UpdatedAtUtc
                """;
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", resultJson);
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task DeleteCategoryAsync(string category, CancellationToken cancellationToken = default) =>
        database.ExecuteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM json_cache WHERE Category=$category";
            command.Parameters.AddWithValue("$category", category);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
