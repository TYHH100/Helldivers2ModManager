using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services.Nexus
{
    [RegisterService(ServiceLifetime.Singleton, Contract = typeof(INexusCacheService))]
    internal sealed class NexusCacheService : INexusCacheService
    {
        public static readonly TimeSpan ModCacheDuration = TimeSpan.FromHours(1);
        public static readonly TimeSpan UpdateGroupCacheDuration = TimeSpan.FromHours(4);

        private readonly ILogger<NexusCacheService> _logger;
        private readonly MemoryCache _cache;

        public NexusCacheService(ILogger<NexusCacheService> logger, IMemoryCache cache)
        {
            _logger = logger;
            _cache = (MemoryCache)cache;
        }

        public async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration)
        {
            if (!_cache.TryGetValue(key, out T? cachedValue))
            {
                _logger.LogDebug("Cache miss for key: {Key}", key);
                cachedValue = await factory();
                
                _cache.Set(key, cachedValue, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = expiration,
                    SlidingExpiration = expiration / 2
                });

                _logger.LogDebug("Cached value for key: {Key} with expiration: {Expiration}", key, expiration);
            }
            else
            {
                _logger.LogDebug("Cache hit for key: {Key}", key);
            }

            return cachedValue!;
        }

        public void Remove(string key)
        {
            _cache.Remove(key);
            _logger.LogDebug("Removed cache key: {Key}", key);
        }

        public void Clear()
        {
            _logger.LogInformation("Clearing all cache entries");
            _cache.Clear();
        }
    }
}