using System.Globalization;
using Helldivers2ModManager.Core.Database;
using Microsoft.Data.Sqlite;

namespace Helldivers2ModManager.Infrastructure.Database;

public sealed class SqliteDatabaseMigrator : IDatabaseMigrator
{
    private const string EnabledModsSql = """
		CREATE TABLE IF NOT EXISTS enabled_mods (
			Guid TEXT PRIMARY KEY NOT NULL,
			Enabled INTEGER NOT NULL DEFAULT 1,
			Toggled TEXT NOT NULL DEFAULT '[]',
			Selected TEXT NOT NULL DEFAULT '[]',
			GroupId TEXT,
			TagIds TEXT,
			SortOrder INTEGER NOT NULL DEFAULT 0
		);
		""";

    private const string ModGroupsSql = """
		CREATE TABLE IF NOT EXISTS mod_groups (
			Id TEXT PRIMARY KEY NOT NULL,
			Name TEXT NOT NULL,
			DisplayIndex INTEGER NOT NULL DEFAULT 0,
			ModGuids TEXT NOT NULL DEFAULT '[]',
			CreatedAtUtc TEXT NOT NULL DEFAULT ''
		);
		CREATE TABLE IF NOT EXISTS group_enabled_mods (
			GroupId TEXT NOT NULL,
			Guid TEXT NOT NULL,
			Enabled INTEGER NOT NULL DEFAULT 1,
			Toggled TEXT NOT NULL DEFAULT '[]',
			Selected TEXT NOT NULL DEFAULT '[]',
			SortOrder INTEGER NOT NULL DEFAULT 0,
			PRIMARY KEY (GroupId, Guid)
		);
		CREATE INDEX IF NOT EXISTS idx_group_enabled_mods_group_sort
			ON group_enabled_mods (GroupId, SortOrder);
		""";

    private const string CacheAndVersionSql = """
		CREATE TABLE IF NOT EXISTS file_hashes (
			ModGuid TEXT NOT NULL,
			FilePath TEXT NOT NULL,
			FileHash TEXT NOT NULL,
			FileSize INTEGER NOT NULL,
			LastModified TEXT NOT NULL,
			PRIMARY KEY (ModGuid, FilePath)
		);
		CREATE INDEX IF NOT EXISTS idx_file_hashes_modguid ON file_hashes (ModGuid);
		CREATE TABLE IF NOT EXISTS version_check_results (
			ModGuid TEXT PRIMARY KEY NOT NULL,
			Status INTEGER NOT NULL DEFAULT 0,
			GameVersion INTEGER NOT NULL DEFAULT 0,
			LastChecked TEXT NOT NULL DEFAULT '',
			ModLastWriteTimeUtc TEXT NOT NULL DEFAULT ''
		);
		CREATE TABLE IF NOT EXISTS game_check_tracker (
			Id INTEGER PRIMARY KEY CHECK (Id = 1),
			ExeLastWriteTimeUtc TEXT NOT NULL DEFAULT ''
		);
		INSERT OR IGNORE INTO game_check_tracker (Id, ExeLastWriteTimeUtc) VALUES (1, '');
		""";

    private static readonly string[] s_criticalTables =
        ["enabled_mods", "mod_groups", "group_enabled_mods"];

    private readonly string _databasePath;

    public SqliteDatabaseMigrator(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public async Task<int> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(SqliteOpenMode.ReadWriteCreate, cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task MigrateAsync(int targetVersion, CancellationToken cancellationToken)
    {
        if (targetVersion is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(targetVersion));

        var parent = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("The database path has no parent directory.");
        Directory.CreateDirectory(parent);
        var existingVersion = await GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        if (existingVersion > targetVersion)
            throw new InvalidOperationException("Database downgrade is not supported.");
        if (existingVersion == targetVersion)
        {
            PruneBackups();
            return;
        }
        await CreateBackupAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(SqliteOpenMode.ReadWriteCreate, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var currentVersion = await GetUserVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var originalCounts = await ReadCriticalRowCountsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (currentVersion < 1 && targetVersion >= 1)
                await ApplyVersionOneAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (currentVersion < 2 && targetVersion >= 2)
                await ApplyVersionTwoAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                $"PRAGMA user_version={targetVersion.ToString(CultureInfo.InvariantCulture)};",
                cancellationToken).ConfigureAwait(false);
            await VerifyIntegrityAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await VerifyRowCountsAsync(connection, transaction, originalCounts, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        PruneBackups();
    }

    private string ConnectionString(SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = false
    }.ToString();

    private async Task<SqliteConnection> OpenConnectionAsync(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString(mode));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task CreateBackupAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath) || new FileInfo(_databasePath).Length == 0)
            return;

        var backupDirectory = BackupDirectory;
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"migration-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.db");
        await using var source = await OpenConnectionAsync(SqliteOpenMode.ReadOnly, cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    private string BackupDirectory => _databasePath + ".migration-backups";

    private void PruneBackups()
    {
        if (!Directory.Exists(BackupDirectory))
            return;
        foreach (var backup in new DirectoryInfo(BackupDirectory)
            .EnumerateFiles("migration-*.db")
            .OrderByDescending(static file => file.CreationTimeUtc)
            .Skip(3))
        {
            backup.Delete();
        }
    }

    private static async Task ApplyVersionOneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, transaction, EnabledModsSql, cancellationToken).ConfigureAwait(false);
        if (!await ColumnExistsAsync(connection, transaction, "enabled_mods", "SortOrder", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "ALTER TABLE enabled_mods ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;",
                cancellationToken).ConfigureAwait(false);
        }
        await ExecuteNonQueryAsync(connection, transaction, ModGroupsSql, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyVersionTwoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, transaction, CacheAndVersionSql, cancellationToken).ConfigureAwait(false);
        if (!await ColumnExistsAsync(connection, transaction, "version_check_results", "ModLastWriteTimeUtc", cancellationToken).ConfigureAwait(false))
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "ALTER TABLE version_check_results ADD COLUMN ModLastWriteTimeUtc TEXT NOT NULL DEFAULT '';",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<Dictionary<string, long>> ReadCriticalRowCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in s_criticalTables)
        {
            if (!await TableExistsAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false))
                continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            counts[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }
        return counts;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.Ordinal))
            throw new InvalidDataException($"SQLite integrity check failed: {result}");
    }

    private static async Task VerifyRowCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, long> expectedMinimums,
        CancellationToken cancellationToken)
    {
        var actual = await ReadCriticalRowCountsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        foreach (var (table, expectedMinimum) in expectedMinimums)
        {
            if (!actual.TryGetValue(table, out var count) || count < expectedMinimum)
                throw new InvalidDataException($"Migration lost rows from critical table {table}.");
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
