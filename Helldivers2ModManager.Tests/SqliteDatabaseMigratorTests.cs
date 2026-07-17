using Helldivers2ModManager.Infrastructure.Database;
using Helldivers2ModManager.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Helldivers2ModManager.Tests;

public sealed class SqliteDatabaseMigratorTests
{
    [Fact]
    public async Task EmptyDatabaseMigratesToLatestSchemaAndPassesIntegrityCheck()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = System.IO.Path.Combine(temporaryDirectory.Path, "mod_manager.db");
        var migrator = new SqliteDatabaseMigrator(databasePath);

        await migrator.MigrateAsync(2, CancellationToken.None);

        Assert.Equal(2, await migrator.GetCurrentVersionAsync(CancellationToken.None));
        await using var connection = await OpenAsync(databasePath);
        Assert.Equal("ok", await ScalarAsync<string>(connection, "PRAGMA integrity_check;"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM game_check_tracker;"));
    }

    [Fact]
    public async Task LegacyDatabaseMigrationPreservesRowsAddsColumnsAndCreatesBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = System.IO.Path.Combine(temporaryDirectory.Path, "mod_manager.db");
        await using (var connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, """
				CREATE TABLE enabled_mods (
					Guid TEXT PRIMARY KEY NOT NULL,
					Enabled INTEGER NOT NULL DEFAULT 1,
					Toggled TEXT NOT NULL DEFAULT '[]',
					Selected TEXT NOT NULL DEFAULT '[]',
					GroupId TEXT,
					TagIds TEXT
				);
				INSERT INTO enabled_mods (Guid, Enabled, Toggled, Selected)
				VALUES ('legacy-guid', 1, '[]', '[]');
				""");
        }
        var migrator = new SqliteDatabaseMigrator(databasePath);

        await migrator.MigrateAsync(2, CancellationToken.None);

        await using var migrated = await OpenAsync(databasePath);
        Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM enabled_mods;"));
        Assert.Equal(1L, await ScalarAsync<long>(
            migrated,
            "SELECT COUNT(*) FROM pragma_table_info('enabled_mods') WHERE name='SortOrder';"));
        Assert.Single(Directory.EnumerateFiles(databasePath + ".migration-backups", "migration-*.db"));
    }

    [Fact]
    public async Task MigrationBackupRetentionKeepsOnlyThreeMostRecentCopies()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = System.IO.Path.Combine(temporaryDirectory.Path, "mod_manager.db");
        var migrator = new SqliteDatabaseMigrator(databasePath);
        await migrator.MigrateAsync(2, CancellationToken.None);
        var backupDirectory = databasePath + ".migration-backups";
        Directory.CreateDirectory(backupDirectory);
        for (var index = 0; index < 5; index++)
        {
            var backup = System.IO.Path.Combine(backupDirectory, $"migration-test-{index}.db");
            await File.WriteAllTextAsync(backup, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.SetCreationTimeUtc(backup, DateTime.UtcNow.AddMinutes(index));
        }
        await migrator.MigrateAsync(2, CancellationToken.None);

        Assert.Equal(
            3,
            Directory.EnumerateFiles(databasePath + ".migration-backups", "migration-*.db").Count());
    }

    [Fact]
    public async Task Version15DatabaseSamplePreservesProfilesGroupsTagsCachesAndVersionHistory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var databasePath = System.IO.Path.Combine(temporaryDirectory.Path, "mod_manager.db");
        var modId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        await using (var connection = await OpenAsync(databasePath))
        {
            await ExecuteAsync(connection, $$"""
				CREATE TABLE enabled_mods (
					Guid TEXT PRIMARY KEY NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1,
					Toggled TEXT NOT NULL DEFAULT '[]', Selected TEXT NOT NULL DEFAULT '[]',
					GroupId TEXT, TagIds TEXT, SortOrder INTEGER NOT NULL DEFAULT 0);
				CREATE TABLE mod_groups (
					Id TEXT PRIMARY KEY NOT NULL, Name TEXT NOT NULL,
					DisplayIndex INTEGER NOT NULL DEFAULT 0, ModGuids TEXT NOT NULL DEFAULT '[]',
					CreatedAtUtc TEXT NOT NULL DEFAULT '');
				CREATE TABLE group_enabled_mods (
					GroupId TEXT NOT NULL, Guid TEXT NOT NULL, Enabled INTEGER NOT NULL DEFAULT 1,
					Toggled TEXT NOT NULL DEFAULT '[]', Selected TEXT NOT NULL DEFAULT '[]',
					SortOrder INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (GroupId, Guid));
				CREATE TABLE file_hashes (
					ModGuid TEXT NOT NULL, FilePath TEXT NOT NULL, FileHash TEXT NOT NULL,
					FileSize INTEGER NOT NULL, LastModified TEXT NOT NULL,
					PRIMARY KEY (ModGuid, FilePath));
				CREATE TABLE version_check_results (
					ModGuid TEXT PRIMARY KEY NOT NULL, Status INTEGER NOT NULL DEFAULT 0,
					GameVersion INTEGER NOT NULL DEFAULT 0, LastChecked TEXT NOT NULL DEFAULT '',
					ModLastWriteTimeUtc TEXT NOT NULL DEFAULT '');
				CREATE TABLE game_check_tracker (
					Id INTEGER PRIMARY KEY CHECK (Id = 1), ExeLastWriteTimeUtc TEXT NOT NULL DEFAULT '');
				INSERT INTO enabled_mods VALUES ('{{modId}}', 1, '[true,false]', '[1,0]', NULL, '["{{tagId}}"]', 4);
				INSERT INTO mod_groups VALUES ('{{groupId}}', 'Legacy Group', 2, '["{{modId}}"]', '2025-01-02T03:04:05.0000000Z');
				INSERT INTO group_enabled_mods VALUES ('{{groupId}}', '{{modId}}', 0, '[false,true]', '[0,1]', 7);
				INSERT INTO file_hashes VALUES ('{{modId}}', 'data/example.bin', 'ABCDEF', 1234, '2025-01-02T03:04:05.0000000Z');
				INSERT INTO version_check_results VALUES ('{{modId}}', 2, 11259375, '2025-01-02T03:04:05.0000000Z', '2025-01-02T02:00:00.0000000Z');
				INSERT INTO game_check_tracker VALUES (1, '2025-01-02T01:00:00.0000000Z');
				PRAGMA user_version=0;
				""");
        }

        var migrator = new SqliteDatabaseMigrator(databasePath);
        await migrator.MigrateAsync(2, CancellationToken.None);

        Assert.Equal(2, await migrator.GetCurrentVersionAsync(CancellationToken.None));
        await using (var migrated = await OpenAsync(databasePath))
        {
            Assert.Equal("ok", await ScalarAsync<string>(migrated, "PRAGMA integrity_check;"));
            Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM enabled_mods;"));
            Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM mod_groups;"));
            Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM group_enabled_mods;"));
            Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM file_hashes;"));
            Assert.Equal(1L, await ScalarAsync<long>(migrated, "SELECT COUNT(*) FROM version_check_results;"));
            Assert.Equal("ABCDEF", await ScalarAsync<string>(migrated, "SELECT FileHash FROM file_hashes;"));
        }

        using (var database = new DatabaseService(NullLogger<DatabaseService>.Instance))
        {
            var enabled = new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database)
                .LoadAll(temporaryDirectory.Path);
            var profile = Assert.Single(enabled);
            Assert.Equal(modId, profile.Guid);
            Assert.Equal(tagId, Assert.Single(profile.TagIds!));
            Assert.Equal([true, false], profile.Toggled);

            var groups = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, database);
            var group = Assert.Single(groups.LoadGroups(temporaryDirectory.Path));
            Assert.Equal(groupId, group.Id);
            Assert.Equal(modId, Assert.Single(group.ModGuids));
            var state = Assert.Single(groups.LoadStates(temporaryDirectory.Path, groupId));
            Assert.Equal(modId, state.Guid);
            Assert.False(state.Enabled);
            Assert.Equal(7, state.SortOrder);
        }

        Assert.Single(Directory.EnumerateFiles(databasePath + ".migration-backups", "migration-*.db"));
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Query returned null."));
    }
}
