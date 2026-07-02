using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Helldivers2ModManager.Services;

/// <summary>
/// SQLite 数据库服务，负责管理数据库初始化及为每次操作提供独立连接。
/// WAL 模式下，独立连接可实现真正的并发读写（读者不阻塞写者，写者不阻塞读者）。
/// 作为 Singleton 注册，共享连接字符串和初始化状态。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class DatabaseService : IDisposable
{
	private const string DatabaseFileName = "mod_manager.db";
	private const string WalPragma = "PRAGMA journal_mode=WAL;";
	private const string BusyTimeoutPragma = "PRAGMA busy_timeout=5000;";

	/// <summary>
	/// 数据库表创建 SQL —— 存储 Mod 启用状态及选项配置
	/// </summary>
	private const string CreateEnabledModsTableSql = @"
		CREATE TABLE IF NOT EXISTS enabled_mods (
			Guid TEXT PRIMARY KEY NOT NULL,
			Enabled INTEGER NOT NULL DEFAULT 1,
			Toggled TEXT NOT NULL DEFAULT '[]',
			Selected TEXT NOT NULL DEFAULT '[]',
			GroupId TEXT,
			TagIds TEXT,
			SortOrder INTEGER NOT NULL DEFAULT 0
		);
	";

	/// <summary>
	/// 为旧版本数据库添加 SortOrder 列的迁移 SQL（在 PRAGMA 侧确保兼容旧库）
	/// </summary>
	private const string AddSortOrderColumnSql = "ALTER TABLE enabled_mods ADD COLUMN SortOrder INTEGER NOT NULL DEFAULT 0;";

	/// <summary>
	/// 检查 SortOrder 列是否已存在的 SQL
	/// </summary>
	private const string CheckSortOrderColumnSql = "SELECT COUNT(*) FROM pragma_table_info('enabled_mods') WHERE name='SortOrder';";

	/// <summary>
	/// 数据库表创建 SQL —— 存储文件哈希缓存，用于模组增量更新时的快速比对
	/// </summary>
	private const string CreateFileHashesTableSql = @"
		CREATE TABLE IF NOT EXISTS file_hashes (
			ModGuid TEXT NOT NULL,
			FilePath TEXT NOT NULL,
			FileHash TEXT NOT NULL,
			FileSize INTEGER NOT NULL,
			LastModified TEXT NOT NULL,
			PRIMARY KEY (ModGuid, FilePath)
		);
	";

	/// <summary>
	/// file_hashes 表索引 —— 加速按 ModGuid 查询
	/// </summary>
	private const string CreateFileHashesIndexSql = "CREATE INDEX IF NOT EXISTS idx_file_hashes_modguid ON file_hashes (ModGuid);";

	/// <summary>
	/// 数据库表创建 SQL —— 存储版本兼容性检查结果
	/// </summary>
	private const string CreateVersionCheckResultsTableSql = @"
		CREATE TABLE IF NOT EXISTS version_check_results (
			ModGuid TEXT PRIMARY KEY NOT NULL,
			Status INTEGER NOT NULL DEFAULT 0,
			GameVersion INTEGER NOT NULL DEFAULT 0,
			LastChecked TEXT NOT NULL DEFAULT '',
			ModLastWriteTimeUtc TEXT NOT NULL DEFAULT ''
		);
	";

	private const string AddModLastWriteTimeColumnSql = "ALTER TABLE version_check_results ADD COLUMN ModLastWriteTimeUtc TEXT NOT NULL DEFAULT '';";

	private const string CheckModLastWriteTimeColumnSql = "SELECT COUNT(*) FROM pragma_table_info('version_check_results') WHERE name='ModLastWriteTimeUtc';";

	/// <summary>
	/// 数据库表创建 SQL —— 存储游戏 exe 最后写入时间，用于检测游戏版本变化
	/// </summary>
	private const string CreateGameCheckTrackerTableSql = @"
		CREATE TABLE IF NOT EXISTS game_check_tracker (
			Id INTEGER PRIMARY KEY CHECK (Id = 1),
			ExeLastWriteTimeUtc TEXT NOT NULL DEFAULT ''
		);
	";

	/// <summary>
	/// 插入默认行（仅当表为空时），确保始终有一条记录
	/// </summary>
	private const string InsertGameCheckTrackerDefaultSql = @"
		INSERT OR IGNORE INTO game_check_tracker (Id, ExeLastWriteTimeUtc) VALUES (1, '');
	";

	private readonly ILogger<DatabaseService> _logger;
	private string? _connectionString;
	private bool _initialized;
	private readonly object _initLock = new();

	public DatabaseService(ILogger<DatabaseService> logger)
	{
		_logger = logger;
	}

	/// <summary>
	/// 是否已初始化
	/// </summary>
	public bool IsInitialized => _initialized;

	/// <summary>
	/// 获取数据库文件路径
	/// </summary>
	public static string GetDatabasePath(string storageDirectory)
	{
		return Path.Combine(storageDirectory, DatabaseFileName);
	}

	/// <summary>
	/// 创建并返回一个已打开的新数据库连接。调用方负责 Dispose。
	/// 每次操作使用独立连接，配合 WAL 模式实现真正的并发读写。
	/// </summary>
	/// <param name="storageDirectory">存储目录路径</param>
	/// <returns>已打开的新 SqliteConnection</returns>
	public SqliteConnection OpenConnection(string storageDirectory)
	{
		EnsureInitialized(storageDirectory);

		var connection = new SqliteConnection(_connectionString);
		connection.Open();
		return connection;
	}

	/// <summary>
	/// 确保数据库文件存在、WAL 模式已启用、表结构已创建。仅首次调用时执行。
	/// </summary>
	private void EnsureInitialized(string storageDirectory)
	{
		if (_initialized)
			return;

		lock (_initLock)
		{
			if (_initialized)
				return;

			try
			{
				var dbPath = GetDatabasePath(storageDirectory);

				// 确保存储目录存在
				if (!Directory.Exists(storageDirectory))
					Directory.CreateDirectory(storageDirectory);

				_logger.LogInformation("Initializing SQLite database: {DbPath}", dbPath);

				var csb = new SqliteConnectionStringBuilder
				{
					DataSource = dbPath,
					Mode = SqliteOpenMode.ReadWriteCreate,
					// Shared Cache —— 多个连接共享同一个内存缓存，提高性能
					Cache = SqliteCacheMode.Shared,
				};
				_connectionString = csb.ToString();

				// 使用临时连接执行初始化
				using var initConnection = new SqliteConnection(_connectionString);
				initConnection.Open();

				// 启用 WAL 模式 —— 允许多读者与一写者并发
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = WalPragma;
					cmd.ExecuteNonQuery();
				}

				// 设置忙等待超时 —— 遇到锁时等待最多 5 秒而非立即失败
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = BusyTimeoutPragma;
					cmd.ExecuteNonQuery();
				}

				// 创建表结构
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = CreateEnabledModsTableSql;
					cmd.ExecuteNonQuery();
				}

				// 迁移旧库：检查并添加 SortOrder 列
				using (var checkCmd = initConnection.CreateCommand())
				{
					checkCmd.CommandText = CheckSortOrderColumnSql;
					var exists = (long)checkCmd.ExecuteScalar()!;
					if (exists == 0)
					{
						using var alterCmd = initConnection.CreateCommand();
						alterCmd.CommandText = AddSortOrderColumnSql;
						alterCmd.ExecuteNonQuery();
						_logger.LogInformation("Added SortOrder column to legacy database");
					}
				}

				// 创建文件哈希缓存表
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = CreateFileHashesTableSql;
					cmd.ExecuteNonQuery();
				}

				// 创建文件哈希表索引
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = CreateFileHashesIndexSql;
					cmd.ExecuteNonQuery();
				}

				// 创建版本检测结果表
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = CreateVersionCheckResultsTableSql;
					cmd.ExecuteNonQuery();
				}
				using (var checkCmd = initConnection.CreateCommand())
				{
					checkCmd.CommandText = CheckModLastWriteTimeColumnSql;
					var exists = (long)checkCmd.ExecuteScalar()!;
					if (exists == 0)
					{
						using var alterCmd = initConnection.CreateCommand();
						alterCmd.CommandText = AddModLastWriteTimeColumnSql;
						alterCmd.ExecuteNonQuery();
						_logger.LogInformation("Added ModLastWriteTimeUtc column to version check results");
					}
				}

				// 创建游戏版本跟踪表 + 默认行
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = CreateGameCheckTrackerTableSql;
					cmd.ExecuteNonQuery();
				}
				using (var cmd = initConnection.CreateCommand())
				{
					cmd.CommandText = InsertGameCheckTrackerDefaultSql;
					cmd.ExecuteNonQuery();
				}

				_initialized = true;
				_logger.LogInformation("SQLite database initialization complete (WAL mode, one connection per operation)");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SQLite 数据库初始化失败");
				_connectionString = null;
				throw;
			}
		}
	}

	/// <summary>
	/// 释放资源
	/// </summary>
	public void Dispose()
	{
		if (_connectionString is not null)
		{
			try
			{
				// 使用临时连接执行 WAL checkpoint，确保数据持久化
				using var conn = new SqliteConnection(_connectionString);
				conn.Open();
				using var cmd = conn.CreateCommand();
				cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
				cmd.ExecuteNonQuery();
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "WAL checkpoint 执行失败");
			}

			_connectionString = null;
			_initialized = false;
			_logger.LogInformation("SQLite database connection released");
		}
	}
}
