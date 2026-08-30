using Helldivers2ModManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 护甲覆盖扫描缓存仓储。
/// 以部署配置签名为键，保存最近一次有效的覆盖分析结果，供启动时和重复配置切换时直接复用。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModConflictRepository
{
    private readonly ILogger<ModConflictRepository> _logger;
    private readonly DatabaseService _databaseService;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ModConflictRepository(ILogger<ModConflictRepository> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public ModConflictAnalysisResult? Load(string storageDirectory, string cacheKey)
    {
        using var connection = _databaseService.OpenConnection(storageDirectory);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ResultJson FROM conflict_scan_cache WHERE CacheKey = @CacheKey;";
        cmd.Parameters.AddWithValue("@CacheKey", cacheKey);

        var resultJson = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ModConflictAnalysisResult>(resultJson);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize cached conflict scan result for key {CacheKey}", cacheKey);
            return null;
        }
    }

    public async Task SaveAsync(string storageDirectory, string cacheKey, ModConflictAnalysisResult result)
    {
        await _writeLock.WaitAsync();
        try
        {
            using var connection = _databaseService.OpenConnection(storageDirectory);
            using var transaction = connection.BeginTransaction();

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO conflict_scan_cache (CacheKey, ResultJson, UpdatedUtc)
                    VALUES (@CacheKey, @ResultJson, @UpdatedUtc)
                    ON CONFLICT(CacheKey) DO UPDATE SET
                        ResultJson = excluded.ResultJson,
                        UpdatedUtc = excluded.UpdatedUtc;
                ";

                cmd.Parameters.AddWithValue("@CacheKey", cacheKey);
                cmd.Parameters.AddWithValue("@ResultJson", JsonSerializer.Serialize(result));
                cmd.Parameters.AddWithValue("@UpdatedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

                cmd.ExecuteNonQuery();
                transaction.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save cached conflict scan result for key {CacheKey}", cacheKey);
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
