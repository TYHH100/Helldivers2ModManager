using Helldivers2ModManager.Models.Nexus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services.Nexus
{
    [RegisterService(ServiceLifetime.Singleton, Contract = typeof(INexusModsService))]
    internal sealed class NexusModsService : INexusModsService
    {
        private const string Helldivers2GameDomain = "helldivers2";

        private readonly ILogger<NexusModsService> _logger;
        private readonly INexusHttpClient _httpClient;
        private readonly INexusCacheService _cacheService;

        public bool Initialized => _httpClient.Initialized;

        public NexusModsService(ILogger<NexusModsService> logger, INexusHttpClient httpClient, INexusCacheService cacheService)
        {
            _logger = logger;
            _httpClient = httpClient;
            _cacheService = cacheService;
        }

        public void Init(string apiKey)
        {
            _httpClient.Init(apiKey);
            _logger.LogInformation("Nexus Mods Service initialized");
        }

        public async Task<Mod> GetModAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Getting mod info for {ModId} in {GameDomain}", modId, gameDomain);

            var cacheKey = $"mod_{gameDomain}_{modId}";
            var wrapper = await _cacheService.GetOrAddAsync(cacheKey, async () =>
            {
                var path = $"games/{gameDomain}/mods/{modId}";
                return await _httpClient.GetAsync<ModWrapper>(path, cancellationToken);
            }, NexusCacheService.ModCacheDuration);

            return wrapper.Data;
        }

        public async Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting mod files for {ModId} in {GameDomain}", modId, gameDomain);

        // 首先获取模组信息来得到全局ID
        var mod = await GetModAsync(gameDomain, modId, cancellationToken);
        
        var allFiles = new List<ModFile>();

        try
        {
            // 获取更新组
            var updateGroups = await GetUpdateGroupsAsync(mod.Id, cancellationToken);
            
            foreach (var group in updateGroups)
            {
                if (group.IsActive == true)
                {
                    var versions = await GetUpdateGroupVersionsAsync(group.Id, cancellationToken);
                    
                    foreach (var version in versions)
                    {
                        if (version.File != null)
                        {
                            allFiles.Add(version.File);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get update groups, fallback to single file method");
            
            // 回退方案：尝试直接获取单个文件（虽然这可能不对）
            var cacheKey = $"modfiles_{gameDomain}_{modId}";
            try
            {
                var wrapper = await _cacheService.GetOrAddAsync(cacheKey, async () =>
                {
                    var path = $"games/{gameDomain}/mod-files/{modId}";
                    return await _httpClient.GetAsync<ModFilesWrapper>(path, cancellationToken);
                }, NexusCacheService.ModCacheDuration);

                if (wrapper.Data != null)
                {
                    allFiles.Add(wrapper.Data);
                }
            }
            catch (Exception innerEx)
            {
                _logger.LogError(innerEx, "Fallback method also failed");
            }
        }
        
        return allFiles;
    }

        public async Task<List<ModFileUpdateGroup>> GetUpdateGroupsAsync(string modId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting update groups for mod {ModId}", modId);

        var cacheKey = $"updategroups_{modId}";
        var wrapper = await _cacheService.GetOrAddAsync(cacheKey, async () =>
        {
            var path = $"mods/{modId}/file-update-groups";
            return await _httpClient.GetAsync<UpdateGroupsWrapper>(path, cancellationToken);
        }, NexusCacheService.UpdateGroupCacheDuration);

        return wrapper.Data?.Groups ?? new List<ModFileUpdateGroup>();
    }

    public async Task<List<ModFileUpdateGroupVersion>> GetUpdateGroupVersionsAsync(string groupId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting versions for update group {GroupId}", groupId);

        var cacheKey = $"updategroupversions_{groupId}";
        var wrapper = await _cacheService.GetOrAddAsync(cacheKey, async () =>
        {
            var path = $"file-update-groups/{groupId}/versions";
            return await _httpClient.GetAsync<UpdateGroupVersionsWrapper>(path, cancellationToken);
        }, NexusCacheService.UpdateGroupCacheDuration);

        return wrapper.Data?.Versions ?? new List<ModFileUpdateGroupVersion>();
    }

        public async Task<List<TrendingMod>> GetTrendingModsAsync(string gameDomain, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting trending mods for {GameDomain}", gameDomain);

        var cacheKey = $"trending_{gameDomain}";
        var wrapper = await _cacheService.GetOrAddAsync(cacheKey, async () =>
        {
            var path = $"games/{gameDomain}/trending-mods";
            return await _httpClient.GetAsync<TrendingModsWrapper>(path, cancellationToken);
        }, NexusCacheService.ModCacheDuration);

        return wrapper.Data?.Mods ?? new List<TrendingMod>();
    }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string modId, string currentVersion, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Checking for updates for mod {ModId}, current version: {CurrentVersion}", modId, currentVersion);

            var updateInfo = new UpdateInfo
            {
                CurrentVersion = currentVersion,
                HasUpdate = false
            };

            try
            {
                var updateGroups = await GetUpdateGroupsAsync(modId, cancellationToken);
                
                if (updateGroups.Count == 0)
                {
                    _logger.LogWarning("No update groups found for mod {ModId}", modId);
                    return updateInfo;
                }

                var activeGroup = updateGroups.FirstOrDefault(g => g.IsActive == true);
                if (activeGroup == null)
                {
                    _logger.LogWarning("No active update group found for mod {ModId}", modId);
                    return updateInfo;
                }

                var versions = await GetUpdateGroupVersionsAsync(activeGroup.Id, cancellationToken);
                
                if (versions.Count == 0)
                {
                    _logger.LogWarning("No versions found for update group {GroupId}", activeGroup.Id);
                    return updateInfo;
                }

                var sortedVersions = versions
                    .Where(v => v.File != null && v.File.UpdateGroupVersion != null)
                    .OrderByDescending(v => ParsePosition(v.Position))
                    .ToList();
                
                if (sortedVersions.Count == 0)
                {
                    _logger.LogWarning("No valid versions found for update group {GroupId}", activeGroup.Id);
                    return updateInfo;
                }
                
                var latestVersion = sortedVersions.First();
                var latestFile = latestVersion.File!;
                var latestUpdateGroup = latestFile.UpdateGroupVersion!;

                var latestPosition = ParsePosition(latestUpdateGroup.Position);
                var currentPosition = ParsePosition("0");

                if (!string.IsNullOrEmpty(currentVersion))
                {
                    foreach (var version in sortedVersions)
                    {
                        var file = version.File!;
                        if (file.Version != null && file.Version.Equals(currentVersion, StringComparison.OrdinalIgnoreCase))
                        {
                            var updateGroup = file.UpdateGroupVersion!;
                            var pos = ParsePosition(updateGroup.Position ?? "0");
                            if (pos > currentPosition)
                            {
                                currentPosition = pos;
                            }
                        }
                    }
                }

                if (latestPosition > currentPosition)
                {
                    updateInfo.HasUpdate = true;
                    updateInfo.LatestVersion = latestFile.Version;
                    updateInfo.LatestModFile = latestFile;
                    _logger.LogInformation("Update available for mod {ModId}: {CurrentVersion} -> {LatestVersion}", 
                        modId, currentVersion, latestFile.Version);
                }
                else
                {
                    updateInfo.LatestVersion = latestFile.Version;
                    _logger.LogInformation("No update available for mod {ModId}, latest version is {LatestVersion}", 
                        modId, latestFile.Version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for updates for mod {ModId}", modId);
            }

            return updateInfo;
        }

        public async Task<string> DownloadModFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Downloading mod file {FileId} for mod {ModId}", fileId, modId);
            return await _httpClient.DownloadFileAsync(gameDomain, modId, fileId, savePath, cancellationToken);
        }

        public void ClearCache()
        {
            _cacheService.Clear();
            _logger.LogInformation("Nexus Mods cache cleared");
        }

        private decimal ParsePosition(string position)
        {
            if (decimal.TryParse(position, out var result))
            {
                return result;
            }
            return 0;
        }
    }
}