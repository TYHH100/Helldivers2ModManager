using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Models;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 冲突扫描缓存的 Core 持久化门面：以部署配置签名为键，复用统一 json_cache 表。
/// </summary>
internal sealed class ModConflictRepository
{
    private const string CacheCategory = "conflict-analysis-v3";

    private readonly JsonCacheRepository _cache;

    public ModConflictRepository(JsonCacheRepository cache)
    {
        _cache = cache;
    }

    public async Task<ModConflictAnalysisResult?> LoadAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var resultJson = await _cache.GetAsync(CacheCategory, cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resultJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ModConflictAnalysisResult>(resultJson);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public Task SaveAsync(string cacheKey, ModConflictAnalysisResult result, CancellationToken cancellationToken = default)
    {
        return _cache.SetAsync(CacheCategory, cacheKey, JsonSerializer.Serialize(result), cancellationToken);
    }
}
