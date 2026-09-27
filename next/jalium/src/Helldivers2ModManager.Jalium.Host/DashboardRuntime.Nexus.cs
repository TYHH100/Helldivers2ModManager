using Helldivers2ModManager.Services.Nexus;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private MemoryCache? _nexusCache;
    private NexusModsService? _nexusService;

    private void OpenNexusDownload()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        _nexusCache ??= new MemoryCache(new MemoryCacheOptions());
        _nexusService ??= new NexusModsService(
            NullLogger<NexusModsService>.Instance,
            new NexusHttpClient(NullLogger<NexusHttpClient>.Instance),
            new NexusCacheService(NullLogger<NexusCacheService>.Instance, _nexusCache));
        _nexusPage?.Dispose();
        _nexusPage = new NexusDownloadPageView(_nexusService, settings, localization,
            ImportArchivesAsync, ShowDashboard, ReportError);
        _layout.PagePresenter.Content = _nexusPage;
    }
}
