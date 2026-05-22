using Helldivers2ModManager.Models.Nexus;

namespace Helldivers2ModManager.Services.Nexus
{
    internal interface INexusHttpClient
    {
        bool Initialized { get; }

        void Init(string apiKey);

        Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default);

        Task<string> DownloadFileAsync(string gameDomain, string modId, string fileId, string savePath, CancellationToken cancellationToken = default);

        void SetApiKey(string apiKey);
    }
}