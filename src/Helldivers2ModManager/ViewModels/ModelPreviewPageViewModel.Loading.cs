using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class ModelPreviewPageViewModel
{
    [RelayCommand]
    private void GoBack() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshMods() => await RefreshModsAsync();

    partial void OnSelectedModChanged(ModData? value)
    {
        if (value is not null)
            _preferredMod = value;

        BuildPreviewOptions(value);
        if (value is not null)
            _ = LoadSelectedModAsync(value, resetView: true);
    }

    partial void OnModelGroupChanged(Model3DGroup? value)
    {
        OnPropertyChanged(nameof(HasModel));
        OnPropertyChanged(nameof(IsAudioOnlyPreview));
    }

    partial void OnSelectedMeshChanged(ModelPreviewMesh? value) => QueueRebuild();

    partial void OnSelectedArmorChanged(ModelPreviewArmorOption? value)
    {
        if (SelectedMesh is not null && !GetArmorMeshes().Contains(SelectedMesh))
            SelectedMesh = GetArmorMeshes().FirstOrDefault(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible);
        OnPropertyChanged(nameof(HasBodyShapeSwitch));
        OnPropertyChanged(nameof(VisibleMeshCount));
        QueueRebuild();
    }

    partial void OnIsolateSelectedMeshChanged(bool value) => QueueRebuild();

    partial void OnShowFilteredMeshesChanged(bool value) => QueueRebuild();

    partial void OnShowStockyBodyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSlimBodySelected));
        OnPropertyChanged(nameof(IsStockyBodySelected));
        if (SelectedMesh is not null && !GetBodyShapeMeshes().Contains(SelectedMesh))
            SelectedMesh = GetBodyShapeMeshes().FirstOrDefault(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible);
        QueueRebuild();
    }

    partial void OnSelectedAnimationChanged(ModelPreviewAnimationChoice? value)
    {
        StopAnimationPlayback();
        ClearAnimationFrameCache();
        _isAnimationApplied = false;
        _suppressAnimationTimeApplication = true;
        try
        {
            AnimationTimeSeconds = 0;
        }
        finally
        {
            _suppressAnimationTimeApplication = false;
        }
        OnPropertyChanged(nameof(SelectedAnimationDuration));
        OnPropertyChanged(nameof(AnimationTimeText));
        QueueRebuild(resetCamera: false);
    }

    partial void OnIsAnimationPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(AnimationPlaybackGlyph));
        OnPropertyChanged(nameof(AnimationPlaybackToolTip));
    }

    partial void OnAnimationTimeSecondsChanged(double value)
    {
        if (!_suppressAnimationTimeApplication && SelectedAnimation is not null)
            _isAnimationApplied = true;
        OnPropertyChanged(nameof(AnimationTimeText));
        QueueAnimationFrame();
    }

    private async Task ReloadAutomaticTexturesAfterResolutionSwitchAsync(ModData mod)
    {
        var loadGeneration = Volatile.Read(ref _loadGeneration);
        var cancellation = BeginTextureLoad();
        try
        {
            await LoadAutomaticTexturePreviewsAsync(mod, Meshes, Textures, loadGeneration, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(mod, loadGeneration))
                return;

            // 纹理分辨率切换后，自动选择的预览贴图也要刷新大图显示
            var preferredTexture = ChoosePreferredTexture();
            if (preferredTexture is not null && _texturePreviews.TryGetValue(preferredTexture.TextureId, out var preferredPreview))
            {
                _selectingAutomaticTexture = true;
                SelectedTexture = preferredTexture;
                _selectingAutomaticTexture = false;
                SelectedTexturePreview = preferredPreview.Image;
            }

            await RebuildModelGroupAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            // 切换开关频繁或换模型时取消即可，不算错误
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reloading automatic texture previews after resolution switch failed");
        }
        finally
        {
            Interlocked.CompareExchange(ref _textureLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    partial void OnSelectedTextureChanged(TextureInspectionItem? value)
    {
        if (_selectingAutomaticTexture || value is null)
            return;

        if (UseAutomaticMaterials)
        {
            UseAutomaticMaterials = false;
            return;
        }

        _ = LoadSelectedTextureAsync(value);
    }

    private Task RefreshModsAsync()
    {
        if (!_modService.Initialized)
        {
            StatusText = _localizationService["ModelPreviewPage.NotReady"];
            return Task.CompletedTask;
        }

        var selection = ModelPreviewModSelection.Resolve(
            _modService.Mods.OrderBy(static mod => mod.Manifest.Name, StringComparer.CurrentCultureIgnoreCase),
            _preferredMod);

        Mods.Clear();
        foreach (var mod in selection.Mods)
            Mods.Add(mod);

        SelectedMod = selection.SelectedMod;
        if (SelectedMod is null)
            StatusText = _localizationService["ModelPreviewPage.EmptyMods"];
        return Task.CompletedTask;
    }

    private async Task LoadSelectedModAsync(ModData mod, bool resetView)
    {
        var cancellation = new CancellationTokenSource();
        _loadCancellation?.Cancel();
        _loadCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        var loadGeneration = Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _renderGeneration);
        IsLoading = true;
        IsInitialLoading = resetView || ModelGroup is null;
        StatusText = _localizationService["ModelPreviewPage.Loading"].Replace("{name}", mod.Manifest.Name);
        ClearActiveTexturePreviews();
        if (resetView)
        {
            ClearRetainedPreviewCaches();
            _gpuSkinningService.ReleaseMeshes(Meshes);
            Meshes.Clear();
            Textures.Clear();
            StopAnimationPlayback();
            Animations.Clear();
            SelectedAnimation = null;
            OnPropertyChanged(nameof(HasAnimations));
            Armors.Clear();
            _selection = new([], 0);
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshCount));
            OnPropertyChanged(nameof(VisibleMeshCount));
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshSummary));
            OnPropertyChanged(nameof(HasBodyShapeSwitch));
            SelectedMesh = null;
            IsolateSelectedMesh = false;
            ShowFilteredMeshes = false;
            ShowStockyBody = true;
            UseOriginalTextureResolution = false;
            UseAutomaticMaterials = true;
            SelectedTexture = null;
            SelectedTexturePreview = null;
            SelectedArmor = null;
            ModelGroup = null;
            SuggestedCameraDistance = 5;
            SuggestedCameraYaw = 0;
            StopAudioPlayback(clearCurrent: true);
            ClearAudioCollections();
            ClearTextCollections();
        }

        try
        {
            // This is the same option expansion used by deployment and conflict checks.
            // It intentionally excludes disabled accessories and the unselected material
            // variant instead of recursively loading every patch beneath the mod folder.
            var selectedPatchFiles = GetPreviewPatchFiles(mod);
            var patchSetKey = CreatePatchSetCacheKey(selectedPatchFiles);
            if (!_modelResultCache.TryGetValue(patchSetKey, out var result))
            {
                result = await _previewBackend.PreviewModelAsync(mod.Directory, selectedPatchFiles, cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    CacheModelResult(patchSetKey, result);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentLoad(mod, loadGeneration))
                return;

            Meshes.Clear();
            Textures.Clear();
            foreach (var mesh in result.Meshes)
                Meshes.Add(mesh);
            foreach (var texture in result.Textures)
                Textures.Add(texture);
            foreach (var armor in result.Armors)
            {
                if (armor.IsAll)
                    armor.Name = _localizationService["ModelPreviewPage.AllArmors"];
                Armors.Add(armor);
            }
            foreach (var library in result.AnimationLibraries)
                foreach (var animation in library.Animations)
                    Animations.Add(new ModelPreviewAnimationChoice(library, animation));
            SelectedAnimation = Animations.FirstOrDefault();
            SelectedArmor = Armors.FirstOrDefault(static armor => armor.IsAll) ?? Armors.FirstOrDefault();

            _selection = ModelPreviewMeshSelector.Select(Meshes);
            UpdateLocalizedPreviewLabels();
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshCount));
            OnPropertyChanged(nameof(VisibleMeshCount));
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshSummary));
            OnPropertyChanged(nameof(HasBodyShapeSwitch));
            OnPropertyChanged(nameof(HasArmorSwitch));
            OnPropertyChanged(nameof(HasAnimations));
            SelectedMesh = GetBodyShapeMeshes().FirstOrDefault(mesh => mesh.RenderStatus == ModelPreviewMeshRenderStatus.Visible) ??
                           GetBodyShapeMeshes().FirstOrDefault();

            await LoadAutomaticTexturePreviewsAsync(mod, Meshes, result.Textures, loadGeneration, cancellationToken);
            if (!IsCurrentLoad(mod, loadGeneration))
                return;

            _selectingAutomaticTexture = true;
            SelectedTexture = ChoosePreferredTexture();
            _selectingAutomaticTexture = false;
            if (SelectedTexture is not null && _texturePreviews.TryGetValue(SelectedTexture.TextureId, out var preferredPreview))
                SelectedTexturePreview = preferredPreview.Image;
            await RebuildModelGroupAsync(cancellationToken);

            StatusText = Meshes.Count > 0
                ? _localizationService["ModelPreviewPage.Loaded"]
                    .Replace("{meshes}", VisibleMeshCount.ToString())
                    .Replace("{triangles}", GetVisibleMeshes().Sum(static mesh => mesh.TriangleCount).ToString())
                    .Replace("{skipped}", result.SkippedStreams.ToString())
                    .Replace("{patches}", result.PatchFileCount.ToString())
                    .Replace("{textures}", _texturePreviews.Count.ToString())
                : _localizationService["ModelPreviewPage.NoGeometry"];
            if (AutomaticallyHiddenMeshCount > 0)
            {
                StatusText += " " + _localizationService["ModelPreviewPage.HiddenOutliers"]
                    .Replace("{count}", AutomaticallyHiddenMeshCount.ToString());
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
                StatusText += " " + result.Error;

            // 多选项全音频模组：直接跳过音频预览（用户决策——每个选项各自携带 bank/stream，
            // 全量解析与原版比对的代价过高，部署后在游戏内体验即可）。
            if (await ShouldSkipAudioPreviewAsync(mod, cancellationToken))
            {
                if (!IsCurrentLoad(mod, loadGeneration))
                    return;
                ApplyAudioInventory(AudioInventoryResult.Empty);
                if (Meshes.Count == 0)
                    StatusText = _localizationService["ModelPreviewPage.AudioMultiOptionSkipped"];
            }
            else
            {
                var audioResult = await LoadAudioInventoryAsync(
                    mod,
                    selectedPatchFiles,
                    patchSetKey,
                    loadGeneration,
                    cancellationToken);
                if (!IsCurrentLoad(mod, loadGeneration))
                    return;
                ApplyAudioInventory(audioResult);
                if (Meshes.Count == 0 && HasAudioEntries)
                {
                    // Audio-only mods have no 3D preview to describe; the audio summary replaces
                    // the "no geometry" status and the audio tab becomes the landing tab.
                    UpdateAudioSummaryStatus(audioResult.PatchCount, audioResult.Error);
                    if (resetView)
                        SelectedPreviewTabIndex = AudioPreviewTabIndex;
                }
            }

            // 字幕/文本预览与音频独立加载（文本模组通常不含音频资源；多选项音频跳过分支里文本仍要预览）。
            var textResult = await LoadTextInventoryAsync(
                mod,
                selectedPatchFiles,
                patchSetKey,
                loadGeneration,
                cancellationToken);
            if (!IsCurrentLoad(mod, loadGeneration))
                return;
            ApplyTextInventory(textResult);
            if (Meshes.Count == 0 && !HasAudioEntries && HasTextEntries)
            {
                UpdateTextSummaryStatus(textResult.PatchCount, textResult.Error);
                if (resetView)
                    SelectedPreviewTabIndex = TextPreviewTabIndex;
            }
            else if (Meshes.Count > 0 && resetView)
            {
                SelectedPreviewTabIndex = 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer option/mod selection owns the preview now.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decode model preview for {Mod}", mod.Manifest.Name);
            if (IsCurrentLoad(mod, loadGeneration))
                StatusText = _localizationService["ModelPreviewPage.LoadFailed"].Replace("{message}", ex.Message);
        }
        finally
        {
            if (IsCurrentLoad(mod, loadGeneration))
            {
                IsLoading = false;
                IsInitialLoading = false;
            }
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void BuildPreviewOptions(ModData? mod)
    {
        PreviewOptions.Clear();
        if (mod?.Manifest is V1ModManifest { Options: { } options })
        {
            for (var index = 0; index < options.Count; index++)
            {
                PreviewOptions.Add(new ModelPreviewOptionViewModel(
                    index,
                    options[index],
                    mod.Directory,
                    index < mod.EnabledOptions.Length && mod.EnabledOptions[index],
                    index < mod.SelectedOptions.Length ? mod.SelectedOptions[index] : 0,
                    PreviewOptionSelectionChanged));
            }
        }
        else if (mod?.Manifest is LegacyModManifest { Options: { Count: > 0 } legacyOptions })
        {
            PreviewOptions.Add(new ModelPreviewOptionViewModel(
                _localizationService["ModelPreviewPage.LegacyVariants"],
                legacyOptions,
                mod.SelectedOptions.FirstOrDefault(),
                PreviewOptionSelectionChanged));
        }

        OnPropertyChanged(nameof(HasPreviewOptions));
        OnPropertyChanged(nameof(HasNoPreviewOptions));
    }

    private void PreviewOptionSelectionChanged()
    {
        if (SelectedMod is { } mod)
            _ = LoadSelectedModAsync(mod, resetView: false);
    }

    private IReadOnlyList<FileInfo> GetPreviewPatchFiles(ModData mod)
    {
        if (mod.Manifest is LegacyModManifest { Options: { Count: > 0 } } && PreviewOptions.Count == 1)
        {
            return _modService.GetSelectedPatchFiles(
                mod,
                mod.EnabledOptions,
                [PreviewOptions[0].SelectedSubOptionIndex]);
        }

        if (mod.Manifest is not V1ModManifest { Options: { } options } || PreviewOptions.Count != options.Count)
            return _modService.GetSelectedPatchFiles(mod);

        return _modService.GetSelectedPatchFiles(
            mod,
            PreviewOptions.Select(static option => option.Enabled).ToArray(),
            PreviewOptions.Select(static option => option.SelectedSubOptionIndex).ToArray());
    }
}
