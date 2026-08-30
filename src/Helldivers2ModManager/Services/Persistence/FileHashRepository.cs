using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 文件哈希缓存的 SQLite 仓储，负责持久化 mod 文件的 SHA-256 哈希值。
/// 通过缓存文件哈希值，避免增量更新时重复计算未变化文件的哈希，大幅提升更新效率。
/// 缓存失效策略：以文件的 LastWriteTimeUtc + FileSize 作为键，任一发生变化则重新计算。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class FileHashRepository
{
    private readonly ILogger<FileHashRepository> _logger;
    private readonly DatabaseService _databaseService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileHashRepository(ILogger<FileHashRepository> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    /// <summary>
    /// 缓存中的单条文件哈希记录
    /// </summary>
    public sealed record CachedFileHash
    {
        public required string FilePath { get; init; }
        public required string FileHash { get; init; }
        public required long FileSize { get; init; }
        public required DateTime LastModified { get; init; }
    }

    /// <summary>
    /// 获取指定 mod 所有文件的缓存哈希记录。
    /// 返回以文件路径为键的字典，用于快速查找。
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="modGuid">mod 的 Guid</param>
    /// <returns>文件路径 → 缓存哈希记录的字典</returns>
    public Dictionary<string, CachedFileHash> GetAllForMod(string storageDirectory, Guid modGuid)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        var results = new Dictionary<string, CachedFileHash>(StringComparer.OrdinalIgnoreCase);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT FilePath, FileHash, FileSize, LastModified FROM file_hashes WHERE ModGuid = @ModGuid;";
        cmd.Parameters.AddWithValue("@ModGuid", modGuid.ToString());

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var filePath = reader.GetString(0);
            var fileHash = reader.GetString(1);
            var fileSize = reader.GetInt64(2);
            var lastModified = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);

            results[filePath] = new CachedFileHash
            {
                FilePath = filePath,
                FileHash = fileHash,
                FileSize = fileSize,
                LastModified = lastModified,
            };
        }

        _logger.LogDebug("Loaded {Count} cached file hashes for mod {ModGuid}", results.Count, modGuid);
        return results;
    }

    /// <summary>
    /// 检查单个文件是否存在有效的缓存哈希（文件大小和修改时间均匹配）
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="modGuid">mod 的 Guid</param>
    /// <param name="filePath">文件的相对路径</param>
    /// <param name="fileSize">文件的当前大小（字节）</param>
    /// <param name="lastModified">文件的当前最后修改时间（UTC）</param>
    /// <returns>有效的缓存哈希值，如果缓存不存在或已失效则返回 null</returns>
    public string? GetValidCacheHash(
        string storageDirectory,
        Guid modGuid,
        string filePath,
        long fileSize,
        DateTime lastModified)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT FileHash FROM file_hashes
            WHERE ModGuid = @ModGuid AND FilePath = @FilePath
            AND FileSize = @FileSize AND LastModified = @LastModified;
        ";
        cmd.Parameters.AddWithValue("@ModGuid", modGuid.ToString());
        cmd.Parameters.AddWithValue("@FilePath", filePath);
        cmd.Parameters.AddWithValue("@FileSize", fileSize);
        cmd.Parameters.AddWithValue("@LastModified", lastModified.ToString("O"));

        var result = cmd.ExecuteScalar();
        return result as string;
    }

    /// <summary>
    /// 批量保存（插入或替换）mod 文件的哈希记录。使用事务确保原子性。
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="modGuid">mod 的 Guid</param>
    /// <param name="hashes">要保存的哈希记录集合（文件路径 → 哈希信息）</param>
    public async Task UpsertModHashesAsync(
        string storageDirectory,
        Guid modGuid,
        Dictionary<string, (string fileHash, long fileSize, DateTime lastModified)> hashes)
    {
        if (hashes.Count == 0)
            return;

        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO file_hashes (ModGuid, FilePath, FileHash, FileSize, LastModified)
                    VALUES (@ModGuid, @FilePath, @FileHash, @FileSize, @LastModified);
                ";

                var modGuidParam = cmd.Parameters.Add("@ModGuid", SqliteType.Text);
                var filePathParam = cmd.Parameters.Add("@FilePath", SqliteType.Text);
                var fileHashParam = cmd.Parameters.Add("@FileHash", SqliteType.Text);
                var fileSizeParam = cmd.Parameters.Add("@FileSize", SqliteType.Integer);
                var lastModifiedParam = cmd.Parameters.Add("@LastModified", SqliteType.Text);

                modGuidParam.Value = modGuid.ToString();

                foreach (var (filePath, (fileHash, fileSize, lastModified)) in hashes)
                {
                    filePathParam.Value = filePath;
                    fileHashParam.Value = fileHash;
                    fileSizeParam.Value = fileSize;
                    lastModifiedParam.Value = lastModified.ToString("O");
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
                _logger.LogDebug("Saved {Count} file hashes for mod {ModGuid}", hashes.Count, modGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save file hashes for mod {ModGuid}, transaction rolled back", modGuid);
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
    /// 删除指定 mod 的所有文件哈希缓存记录
    /// </summary>
    /// <param name="storageDirectory">存储目录</param>
    /// <param name="modGuid">mod 的 Guid</param>
    public async Task DeleteForModAsync(string storageDirectory, Guid modGuid)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM file_hashes WHERE ModGuid = @ModGuid;";
                cmd.Parameters.AddWithValue("@ModGuid", modGuid.ToString());
                var deleted = cmd.ExecuteNonQuery();

                transaction.Commit();
                if (deleted > 0)
                    _logger.LogDebug("Deleted {Count} cached file hashes for mod {ModGuid}", deleted, modGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file hashes for mod {ModGuid}, transaction rolled back", modGuid);
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
