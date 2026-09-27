using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private void OpenModelPreview(ModData? initialMod)
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        _modelPreviewPage?.Dispose();
        var modService = CreateModService();
        var inspection = new PatchResourceInspectionService();
        _versionService ??= new VersionCheckService(
            NullLogger<VersionCheckService>.Instance, settings, localization);
        var backend = new ModelPreviewBackend(inspection, _versionService,
            NullLogger<ModelPreviewBackend>.Instance);
        Task? initialize = null;

        async Task<ModelPreviewResult> PreviewAsync(ModData mod, bool[] enabled,
            int[] selected, bool forceDecode, CancellationToken token)
        {
            initialize ??= Task.Run(() => modService.Init(settings));
            await initialize.WaitAsync(token);
            var patches = await Task.Run(() =>
            {
                if (mod.Manifest is LegacyModManifest { Options.Count: > 0 })
                    return modService.GetSelectedPatchFiles(mod, mod.EnabledOptions, selected);
                if (mod.Manifest is V1ModManifest { Options: { } options }
                    && enabled.Length == options.Count && selected.Length == options.Count)
                    return modService.GetSelectedPatchFiles(mod, enabled, selected);
                return modService.GetSelectedPatchFiles(mod);
            }, token);
            return await backend.PreviewModelAsync(mod.Directory, patches, forceDecode, token);
        }

        _modelPreviewPage = new ModelPreviewPageView(localization,
            () => workspace.Mods, PreviewAsync,
            (mod, texture, limit, token) => inspection.PreviewTextureAsync(
                mod.Directory, texture, limit, token),
            ShowDashboard,
            mod =>
            {
                _modelPreviewPage?.Dispose();
                _modelPreviewPage = null;
                OpenPatchResourceViewer(mod);
            }, initialMod);
        _layout.PagePresenter.Content = _modelPreviewPage;
    }
}
