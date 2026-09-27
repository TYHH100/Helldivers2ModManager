using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Parsing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private TextBankInspectionService? _textInspection;
    private AudioBankInspectionService? _audioInspection;
    private LuaScriptInspectionService? _luaInspection;

    private void OpenPatchResourceViewer(ModData? initialMod)
    {
        if (_layout is null || _tasks is null)
            throw new InvalidOperationException("Window is not ready.");
        _patchViewerPage?.Dispose();
        var modService = CreateModService();
        var inspection = new PatchResourceInspectionService();
        _textInspection ??= new TextBankInspectionService(
            NullLogger<TextBankInspectionService>.Instance, settings);
        _audioInspection ??= new AudioBankInspectionService(
            NullLogger<AudioBankInspectionService>.Instance, settings);
        _luaInspection ??= new LuaScriptInspectionService(
            NullLogger<LuaScriptInspectionService>.Instance);
        var textInspection = _textInspection;
        var audioInspection = _audioInspection;
        var luaInspection = _luaInspection;
        var typeDetection = new ModTypeDetectionService(
            NullLogger<ModTypeDetectionService>.Instance);
        Task? initialize = null;

        async Task<IReadOnlyList<FileInfo>> GetSelectedPatchesAsync(ModData mod,
            CancellationToken cancellationToken)
        {
            initialize ??= Task.Run(() => modService.Init(settings));
            await initialize.WaitAsync(cancellationToken);
            return await Task.Run(() => modService.GetSelectedPatchFiles(mod),
                cancellationToken);
        }

        async Task<PatchResourceInspectionResult> InspectAsync(ModData mod,
            CancellationToken cancellationToken) => await inspection.InspectAsync(mod.Directory,
                await GetSelectedPatchesAsync(mod, cancellationToken), cancellationToken);

        _patchViewerPage = new PatchResourceViewerPageView(localization,
            () => workspace.Mods, InspectAsync,
            (mod, texture, limit, token) => inspection.PreviewTextureAsync(
                mod.Directory, texture, limit, token),
            async (mod, token) =>
            {
                if (mod.Manifest is V1ModManifest { Options.Count: > 1 }
                    && await Task.Run(() => typeDetection.Detect(mod.Directory).Type == ModType.Audio, token))
                    return null;
                return await audioInspection.InspectAsync(mod.Directory,
                    await GetSelectedPatchesAsync(mod, token), token);
            },
            async (mod, token) => await textInspection.InspectAsync(mod.Directory,
                await GetSelectedPatchesAsync(mod, token), token),
            async (mod, token) =>
            {
                var patches = await GetSelectedPatchesAsync(mod, token);
                return await luaInspection.InspectAsync(mod.Directory, patches, token);
            },
            async (mod, destination, token) =>
            {
                var patches = await GetSelectedPatchesAsync(mod, token);
                return await luaInspection.ExtractAsync(mod.Directory, patches, destination, token);
            }, ShowDashboard, initialMod);
        _layout.PagePresenter.Content = _patchViewerPage;
    }
}
