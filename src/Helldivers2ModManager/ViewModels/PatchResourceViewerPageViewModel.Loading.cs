using CommunityToolkit.Mvvm.ComponentModel;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class PatchResourceViewerPageViewModel
{
    private CancellationTokenSource? _loadCancellation;
    private readonly CancellationTokenSource _pageLifetimeCancellation = new();
    private int _loadGeneration;
    private bool _isDisposed;
    internal Task PendingResourceLoad { get; private set; } = Task.CompletedTask;

    private bool IsCurrentLoad(ModData mod, int generation) =>
        !_isDisposed && ReferenceEquals(mod, SelectedMod) && generation == _loadGeneration;

    private void CancelResourceLoad()
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellation?.Cancel();
        _loadCancellation = null;
    }

    private void ClearResourceCollections()
    {
        StopAudioPlayback(clearCurrent: true);
        ClearAudioCollections();
        ClearTextCollections();
        ClearLuaCollections();
        TocEntries.Clear();
        GpuStreams.Clear();
        Textures.Clear();
        SelectedTexture = null;
        _loadedTexturePreview = null;
        TexturePreview = null;
        IsLoading = false;
    }

    private async Task LoadSelectedModAsync(ModData mod, bool resetView = true)
    {
        CancelResourceLoad();
        ClearResourceCollections();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_pageLifetimeCancellation.Token);
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        var generation = _loadGeneration;
        if (resetView)
        {
            SelectedPreviewTabIndex = 0;
            ClearAudioInventoryCache();
            ClearTextInventoryCache();
            _luaInventoryCache.Clear();
            _luaInventoryOrder.Clear();
        }
        IsLoading = true;
        StatusText = _localizationService["PatchResourceViewerPage.Loading"].Replace("{name}", mod.Manifest.Name);
        TexturePreviewStatus = _localizationService["PatchResourceViewerPage.SelectTexture"];

        try
        {
            // Every resource tab and extraction uses the mod's current deployment selection.
            var patchFiles = _modService.GetSelectedPatchFiles(mod);
            var key = CreatePatchSetCacheKey(patchFiles);
            var result = await _inspectionService.InspectAsync(mod.Directory, patchFiles, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(mod, generation))
                return;
            foreach (var item in result.TocEntries)
                TocEntries.Add(item);
            foreach (var item in result.GpuStreams)
                GpuStreams.Add(item);
            foreach (var item in result.Textures)
                Textures.Add(item);
            SelectedTexture = Textures.FirstOrDefault();
            StatusText = _localizationService["PatchResourceViewerPage.Loaded"]
                .Replace("{patches}", result.PatchFileCount.ToString())
                .Replace("{toc}", TocEntries.Count.ToString())
                .Replace("{streams}", GpuStreams.Count.ToString());
            if (!string.IsNullOrWhiteSpace(result.Error))
                StatusText += " " + result.Error;

            if (await ShouldSkipAudioPreviewAsync(mod, token))
            {
                if (!IsCurrentLoad(mod, generation))
                    return;
                StatusText += " " + _localizationService["ModelPreviewPage.AudioMultiOptionSkipped"];
            }
            else
            {
                var audio = await LoadAudioInventoryAsync(mod, patchFiles, key, generation, token);
                if (!IsCurrentLoad(mod, generation))
                    return;
                ApplyAudioInventory(audio);
                if (!string.IsNullOrWhiteSpace(audio.Error))
                {
                    AudioMessageText = _localizationService["ModelPreviewPage.AudioLoadFailed"].Replace("{message}", audio.Error);
                    StatusText += " " + AudioMessageText;
                }
                if (audio.UncomparedEntries > 0)
                    AudioMessageText += " " + _localizationService["ModelPreviewPage.AudioUncomparedHint"]
                        .Replace("{count}", audio.UncomparedEntries.ToString("N0"));
            }

            var text = await LoadTextInventoryAsync(mod, patchFiles, key, generation, token);
            if (!IsCurrentLoad(mod, generation))
                return;
            ApplyTextInventory(text);
            if (!string.IsNullOrWhiteSpace(text.Error))
                StatusText += " " + _localizationService["ModelPreviewPage.TextLoadFailed"].Replace("{message}", text.Error);

            var lua = await LoadLuaInventoryAsync(mod, patchFiles, key, generation, token);
            if (!IsCurrentLoad(mod, generation))
                return;
            ApplyLuaInventory(lua);
            if (!string.IsNullOrWhiteSpace(lua.Error))
            {
                LuaMessageText = _localizationService["ModelPreviewPage.LuaLoadFailed"].Replace("{message}", lua.Error);
                StatusText += " " + LuaMessageText;
                OnPropertyChanged(nameof(HasLuaMessage));
            }

            // Pure resource mods land on their useful tab; geometry mods retain the TOC.
            if (resetView && !TocEntries.Any(entry => entry.TypeId == PatchResourceTypeIds.Unit))
                SelectedPreviewTabIndex = HasAudioEntries ? AudioPreviewTabIndex :
                    HasTextEntries ? TextPreviewTabIndex : HasLuaEntries ? LuaScriptsPreviewTabIndex : 0;
            else if ((SelectedPreviewTabIndex == AudioPreviewTabIndex && !HasAudioEntries) ||
                     (SelectedPreviewTabIndex == TextPreviewTabIndex && !HasTextEntries) ||
                     (SelectedPreviewTabIndex == LuaScriptsPreviewTabIndex && !HasLuaEntries))
                SelectedPreviewTabIndex = 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect patch resources for {Mod}", mod.Manifest.Name);
            if (IsCurrentLoad(mod, generation))
                StatusText = _localizationService["PatchResourceViewerPage.LoadFailed"].Replace("{message}", ex.Message);
        }
        finally
        {
            if (IsCurrentLoad(mod, generation))
                IsLoading = false;
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private static string CreatePatchSetCacheKey(IReadOnlyList<FileInfo> patchFiles)
    {
        var key = new System.Text.StringBuilder(patchFiles.Count * 160);
        foreach (var patchFile in patchFiles)
        {
            AppendFileStamp(key, patchFile.FullName);
            AppendFileStamp(key, patchFile.FullName + ".gpu_resources");
            AppendFileStamp(key, patchFile.FullName + ".stream");
        }
        return key.ToString();
    }

    private static void AppendFileStamp(System.Text.StringBuilder key, string path)
    {
        key.Append(path).Append('|');
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            key.Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks);
        }
        key.AppendLine();
    }
}
