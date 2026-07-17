using Helldivers2ModManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 版本兼容性检查结果的 SQLite 仓储。
/// 将每个 Mod 的检查状态持久化到 version_check_results 表，
/// 避免检查结果常驻内存。每次操作创建独立连接，用完即关。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class VersionCheckRepository
{
    private readonly ILogger<VersionCheckRepository> _logger;
    private readonly DatabaseService _databaseService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public VersionCheckRepository(ILogger<VersionCheckRepository> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    /// <summary>
    /// 从数据库加载所有 Mod 的版本检测结果
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <returns>ModGuid → (Status, GameVersion, LastChecked, ModLastWriteTimeUtc) 的映射字典</returns>
    public Dictionary<Guid, (ModVersionStatus Status, uint GameVersion, DateTime LastChecked, DateTime ModLastWriteTimeUtc)> LoadAll(string storageDirectory)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        var results = new Dictionary<Guid, (ModVersionStatus, uint, DateTime, DateTime)>();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ModGuid, Status, GameVersion, LastChecked, ModLastWriteTimeUtc FROM version_check_results;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var guid = Guid.Parse(reader.GetString(0));
                var status = (ModVersionStatus)reader.GetInt32(1);
                var gameVersion = unchecked((uint)reader.GetInt32(2));
                var lastChecked = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var modLastWriteTimeUtc = DateTime.MinValue;
                if (!reader.IsDBNull(4) && DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedModTime))
                    modLastWriteTimeUtc = parsedModTime;

                results[guid] = (status, gameVersion, lastChecked, modLastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "解析版本检测记录失败，跳过");
            }
        }

        _logger.LogDebug("Loaded {Count} version check results from database", results.Count);
        return results;
    }

    /// <summary>
    /// 批量保存（Upsert）版本检测结果，使用事务确保原子性
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="results">ModGuid → (Status, GameVersion, LastChecked, ModLastWriteTimeUtc) 的映射</param>
    public async Task SaveAllAsync(string storageDirectory,
        Dictionary<Guid, (ModVersionStatus Status, uint GameVersion, DateTime LastChecked, DateTime ModLastWriteTimeUtc)> results)
    {
        _databaseService.EnsureWritable(storageDirectory);
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO version_check_results (ModGuid, Status, GameVersion, LastChecked, ModLastWriteTimeUtc)
                    VALUES (@ModGuid, @Status, @GameVersion, @LastChecked, @ModLastWriteTimeUtc)
                    ON CONFLICT(ModGuid) DO UPDATE SET
                        Status = excluded.Status,
                        GameVersion = excluded.GameVersion,
                        LastChecked = excluded.LastChecked,
                        ModLastWriteTimeUtc = excluded.ModLastWriteTimeUtc;
                ";

                var guidParam = cmd.Parameters.Add("@ModGuid", SqliteType.Text);
                var statusParam = cmd.Parameters.Add("@Status", SqliteType.Integer);
                var versionParam = cmd.Parameters.Add("@GameVersion", SqliteType.Integer);
                var checkedParam = cmd.Parameters.Add("@LastChecked", SqliteType.Text);
                var modLastWriteTimeParam = cmd.Parameters.Add("@ModLastWriteTimeUtc", SqliteType.Text);

                foreach (var kvp in results)
                {
                    guidParam.Value = kvp.Key.ToString();
                    statusParam.Value = (int)kvp.Value.Status;
                    versionParam.Value = unchecked((int)kvp.Value.GameVersion);
                    checkedParam.Value = kvp.Value.LastChecked.ToString("O");
                    modLastWriteTimeParam.Value = kvp.Value.ModLastWriteTimeUtc.ToString("O");

                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                _logger.LogInformation("Saved {Count} version check results to database", results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存版本检测结果失败，事务已回滚");
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 删除指定 Mod 的版本检测记录
    /// </summary>
    public async Task DeleteByGuidAsync(string storageDirectory, Guid guid)
    {
        _databaseService.EnsureWritable(storageDirectory);
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM version_check_results WHERE ModGuid = @ModGuid;";
            cmd.Parameters.AddWithValue("@ModGuid", guid.ToString());
            cmd.ExecuteNonQuery();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 获取上次记录的游戏 exe 最后写入时间（UTC），用于检测游戏是否更新
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <returns>上次记录的时间；若从未记录则返回 DateTime.MinValue</returns>
    public DateTime GetGameExeLastWriteTime(string storageDirectory)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ExeLastWriteTimeUtc FROM game_check_tracker WHERE Id = 1;";

        var result = cmd.ExecuteScalar();
        if (result is string s && !string.IsNullOrEmpty(s))
        {
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }
        return DateTime.MinValue;
    }

    /// <summary>
    /// 更新游戏 exe 最后写入时间（UTC），在全量扫描完成后调用
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="lastWriteTimeUtc">exe 的最后写入时间</param>
    public async Task UpdateGameExeLastWriteTimeAsync(string storageDirectory, DateTime lastWriteTimeUtc)
    {
        _databaseService.EnsureWritable(storageDirectory);
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE game_check_tracker SET ExeLastWriteTimeUtc = @Time WHERE Id = 1;";
            cmd.Parameters.AddWithValue("@Time", lastWriteTimeUtc.ToString("O"));
            cmd.ExecuteNonQuery();

            _logger.LogDebug("Updated game exe last write time: {Time}", lastWriteTimeUtc.ToString("O"));
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
