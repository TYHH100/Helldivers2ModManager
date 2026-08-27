using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Persistence;

public sealed class Database : IDisposable
{
    private const int SchemaVersion = 2;
    private readonly ILogger<Database> _logger;
    private readonly object _initializationLock = new();
    private readonly SemaphoreSlim _initializationSemaphore = new(1, 1);
    private string? _connectionString;
    private bool _initialized;
    private bool _disposed;

    public string Path { get; }

    public Database(string path, ILogger<Database>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Database path cannot be empty.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Database>.Instance;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return;

        await _initializationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Task.Run(() => InitializeCore(), cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationSemaphore.Release();
        }
    }

    public async Task<T> ExecuteAsync<T>(Func<SqliteConnection, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await operation(connection).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(Func<SqliteConnection, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync<object?>(async connection =>
        {
            await operation(connection).ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task ExecuteInTransactionAsync(
        SqliteConnection connection,
        Func<SqliteTransaction, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operation);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _initializationSemaphore.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private void InitializeCore()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        MigrateGroupMemberForeignKeys(connection);
        Execute(connection, "PRAGMA foreign_keys=ON;");
        foreach (var statement in CreateSchemaStatements()) Execute(connection, statement);
        Execute(connection, $"PRAGMA user_version={SchemaVersion};");
        _logger.LogInformation("SQLite persistence initialized: {DatabasePath}, schema={SchemaVersion}", Path, SchemaVersion);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void MigrateGroupMemberForeignKeys(SqliteConnection connection)
    {
        var definition = ExecuteText(connection, "SELECT sql FROM sqlite_master WHERE type='TABLE' AND name='mod_group_members'");
        if (string.IsNullOrWhiteSpace(definition) ||
            definition.Contains("FOREIGN KEY(GroupId, ProfileId)", StringComparison.OrdinalIgnoreCase) ||
            definition.Contains("FOREIGN KEY (GroupId, ProfileId)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            ExecuteInTransaction(connection, transaction, "ALTER TABLE mod_group_members RENAME TO mod_group_members_migration");
            ExecuteInTransaction(connection, transaction, """
                CREATE TABLE mod_group_members (
                    ProfileId TEXT NOT NULL,
                    GroupId TEXT NOT NULL,
                    ModGuid TEXT NOT NULL,
                    Enabled INTEGER NOT NULL DEFAULT 0,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    StateJson TEXT NOT NULL DEFAULT '{}',
                    PRIMARY KEY (GroupId, ModGuid),
                    FOREIGN KEY (GroupId, ProfileId) REFERENCES mod_groups(Id, ProfileId) ON DELETE CASCADE,
                    FOREIGN KEY (ProfileId) REFERENCES profiles(Id) ON DELETE CASCADE
                )
                """);
            ExecuteInTransaction(connection, transaction, """
                INSERT INTO mod_group_members(ProfileId,GroupId,ModGuid,Enabled,SortOrder,StateJson)
                SELECT ProfileId,GroupId,ModGuid,Enabled,SortOrder,StateJson FROM mod_group_members_migration
                """);
            ExecuteInTransaction(connection, transaction, "DROP TABLE mod_group_members_migration");
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void ExecuteInTransaction(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string? ExecuteText(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command.ExecuteScalar() as string;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> CreateSchemaStatements()
    {
        yield return """
            CREATE TABLE IF NOT EXISTS profiles (
                Id TEXT PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL,
                IsDefault INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            )
            """;
        yield return """
            CREATE TABLE IF NOT EXISTS mod_groups (
                Id TEXT NOT NULL,
                ProfileId TEXT NOT NULL,
                Name TEXT NOT NULL,
                DisplayIndex INTEGER NOT NULL DEFAULT 0,
                CreatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (Id, ProfileId),
                FOREIGN KEY (ProfileId) REFERENCES profiles(Id) ON DELETE CASCADE
            )
            """;
        yield return """
            CREATE TABLE IF NOT EXISTS mod_states (
                ProfileId TEXT NOT NULL,
                ModGuid TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 0,
                GroupId TEXT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                StateJson TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY (ProfileId, ModGuid),
                FOREIGN KEY (ProfileId) REFERENCES profiles(Id) ON DELETE CASCADE
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS idx_mod_states_profile_sort ON mod_states(ProfileId, SortOrder);";
        yield return "CREATE INDEX IF NOT EXISTS idx_mod_groups_profile_display ON mod_groups(ProfileId, DisplayIndex);";
        yield return """
            CREATE TABLE IF NOT EXISTS mod_group_members (
                ProfileId TEXT NOT NULL,
                GroupId TEXT NOT NULL,
                ModGuid TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 0,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                StateJson TEXT NOT NULL DEFAULT '{}',
                PRIMARY KEY (GroupId, ModGuid),
                FOREIGN KEY (ProfileId) REFERENCES profiles(Id) ON DELETE CASCADE,
                FOREIGN KEY (GroupId, ProfileId) REFERENCES mod_groups(Id, ProfileId) ON DELETE CASCADE
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS idx_mod_group_members_profile_group ON mod_group_members(ProfileId, GroupId, SortOrder);";
        yield return """
            CREATE TABLE IF NOT EXISTS file_hashes (
                ModGuid TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                FileHash TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                LastModifiedUtc TEXT NOT NULL,
                PRIMARY KEY (ModGuid, FilePath)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS idx_file_hashes_mod ON file_hashes(ModGuid);";
        yield return """
            CREATE TABLE IF NOT EXISTS version_results (
                ModGuid TEXT PRIMARY KEY NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                ResultJson TEXT NOT NULL DEFAULT '{}',
                CheckedAtUtc TEXT NOT NULL,
                ModLastWriteTimeUtc TEXT NULL
            )
            """;
        yield return """
            CREATE TABLE IF NOT EXISTS json_cache (
                Category TEXT NOT NULL,
                CacheKey TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                PRIMARY KEY (Category, CacheKey)
            )
            """;
        yield return "CREATE INDEX IF NOT EXISTS idx_json_cache_category_updated ON json_cache(Category, UpdatedAtUtc);";
        yield return """
            CREATE TABLE IF NOT EXISTS game_check_tracker (
                Id INTEGER PRIMARY KEY NOT NULL CHECK(Id = 1),
                ExeLastWriteTimeUtc TEXT NOT NULL
            )
            """;
        yield return """
            CREATE TABLE IF NOT EXISTS preferences (
                Key TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            )
            """;
    }
}
