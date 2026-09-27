using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private void OpenArmorReuse()
    {
        if (_layout is null || _tasks is null)
            throw new InvalidOperationException("Window is not ready.");
        _armorReusePage?.Dispose();
        var modService = CreateModService();
        var versionCheck = new VersionCheckService(
            NullLogger<VersionCheckService>.Instance, settings, localization);
        var armorReuse = new ArmorReuseService(
            NullLogger<ArmorReuseService>.Instance, modService, versionCheck, localization);
        var initialized = false;

        async Task<ArmorReuseAnalysisResult> ScanAsync(
            IReadOnlyList<ModData> mods, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!initialized)
            {
                modService.Init(settings);
                initialized = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await armorReuse.AnalyzeAsync(mods, cancellationToken);
        }

        _armorReusePage = new ArmorReusePageView(localization, _tasks,
            () => workspace.Mods.Where(static mod => mod.Enabled).ToArray(),
            ScanAsync, ShowDashboard);
        _layout.PagePresenter.Content = _armorReusePage;
        _ = _armorReusePage.ScanAsync();
    }
}
