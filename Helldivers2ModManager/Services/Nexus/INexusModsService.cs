using Helldivers2ModManager.Models.Nexus;

namespace Helldivers2ModManager.Services.Nexus
{
    internal interface INexusModsService
    {
        bool Initialized { get; }

        void Init(string apiKey);

        Task<Mod> GetModAsync(string gameDomain, string modId, CancellationToken cancellationToken = default);

        Task<List<ModFile>> GetModFilesAsync(string gameDomain, string modId, CancellationToken cancellationToken = default);

        Task<List<ModFileUpdateGroup>> GetUpdateGroupsAsync(string modId, CancellationToken cancellationToken = default);

        Task<List<ModFileUpdateGroupVersion>> GetUpdateGroupVersionsAsync(string groupId, CancellationToken cancellationToken = default);

        Task<List<TrendingMod>> GetTrendingModsAsync(string gameDomain, CancellationToken cancellationToken = default);

        Task<UpdateInfo> CheckForUpdatesAsync(string modId, string currentVersion, CancellationToken cancellationToken = default);

        Task<string> DownloadModFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default);

        void ClearCache();
    }
}