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

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ModelPreviewPageViewModel : PageViewModelBase
{
    private const int ModelPreviewMaxTexturePixels = 1_048_576; // 1024 x 1024 is sufficient for the viewport.
    // Native resolution is explicitly requested for one manually selected texture.
    // This keeps the ordinary automatic-material path bounded while still allowing a
    // 4K source mip (64 MiB BGRA) to reach WPF without selecting a lower mip. The
    // bound is deliberately below the service's 8K ceiling: 4K already far exceeds
    // the viewport, and capping the largest managed allocation on the LOH at 64 MiB
    // (instead of 256 MiB) keeps repeated previews from leaving large buffers behind.
    private const int ModelPreviewOriginalTexturePixels = 16_777_216;
    private const int MaxAutomaticTexturePreviews = 16;
    private const int MaxActiveTexturePreviewEntries = MaxAutomaticTexturePreviews + 1;
    private const int MaxDecodedTextureCacheEntries = 12;
    private const int MaxModelResultCacheEntries = 1;
    private const int AnimationFramesPerSecond = 20;
    private const int MaxCachedAnimationFrames = 60;
    private const long MaxAnimationFrameCacheBytes = 96L * 1024 * 1024;
    private readonly ILogger<ModelPreviewPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navigationStore;
    private readonly ModService _modService;
    private readonly PatchResourceInspectionService _inspectionService;
    private readonly ModelPreviewBackend _previewBackend;
    private readonly GpuSkinningService _gpuSkinningService;
    private readonly LocalizationService _localizationService;
    private readonly Dictionary<ulong, LoadedTexturePreview> _texturePreviews = [];
    private readonly HashSet<ulong> _automaticTexturePreviewIds = [];
    private readonly Dictionary<TexturePreviewCacheKey, LoadedTexturePreview> _decodedTexturePreviews = [];
    private readonly Queue<TexturePreviewCacheKey> _decodedTextureOrder = [];
    private readonly Dictionary<string, ModelPreviewResult> _modelResultCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _modelResultOrder = [];
    private readonly CancellationTokenSource _pageLifetimeCancellation = new();
    private LoadedTexturePreview? _selectedOriginalTexturePreview;
    private ulong? _selectedOriginalTextureId;
    private ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry> _geometryCache = new();
    private readonly ConcurrentDictionary<AnimationBindingCacheKey, ModelPreviewAnimationBinding> _animationBindings = [];
    private readonly Dictionary<ModelPreviewMesh, MeshGeometry3D> _liveMeshGeometries = [];
    private readonly Dictionary<int, AnimationGeometryUpdate[]> _animationFrameCache = [];
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private readonly DispatcherTimer _animationTimer;
    private readonly Stopwatch _animationClock = new();
    private ModelPreviewSelection _selection = new([], 0);
    private ModData? _preferredMod;
    private int _renderGeneration;
    private int _loadGeneration;
    private bool _selectingAutomaticTexture;
    private ModelPreviewCameraDirection _cameraDirection = ModelPreviewCameraDirection.Front;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _textureLoadCancellation;
    private int _textureLoadGeneration;
    private int _isDisposed;
    private int _rebuildRequested;
    private int _animationFrameRequested;
    private int _animationFrameWorkerRunning;
    private int _cameraResetRequested;
    private bool _isAnimationApplied;
    private bool _suppressAnimationTimeApplication;
    private ModelPreviewAnimationChoice? _animationFrameCacheChoice;
    private int _animationFrameCacheRenderGeneration = -1;

    public override string Title => _localizationService["ModelPreviewPage.Title"];

    public ObservableCollection<ModData> Mods { get; } = [];
    public ObservableCollection<ModelPreviewMesh> Meshes { get; } = [];
    public ObservableCollection<TextureInspectionItem> Textures { get; } = [];
    public ObservableCollection<ModelPreviewArmorOption> Armors { get; } = [];
    public ObservableCollection<ModelPreviewAnimationChoice> Animations { get; } = [];
    public ObservableCollection<ModelPreviewOptionViewModel> PreviewOptions { get; } = [];

    [ObservableProperty]
    private ModData? _selectedMod;

    [ObservableProperty]
    private Model3DGroup? _modelGroup;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isInitialLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _suggestedCameraDistance = 5;

    [ObservableProperty]
    private double _suggestedCameraYaw;

    [ObservableProperty]
    private int _cameraResetVersion;

    [ObservableProperty]
    private ModelPreviewMesh? _selectedMesh;

    [ObservableProperty]
    private bool _isolateSelectedMesh;

    [ObservableProperty]
    private bool _showFilteredMeshes;

    [ObservableProperty]
    private TextureInspectionItem? _selectedTexture;

    [ObservableProperty]
    private ModelPreviewArmorOption? _selectedArmor;

    [ObservableProperty]
    private ImageSource? _selectedTexturePreview;

    [ObservableProperty]
    private bool _useAutomaticMaterials = true;

    [ObservableProperty]
    private bool _useOriginalTextureResolution;

    [ObservableProperty]
    private string _cameraOrientationText = string.Empty;

    [ObservableProperty]
    private bool _showStockyBody = true;

    [ObservableProperty]
    private ModelPreviewAnimationChoice? _selectedAnimation;

    [ObservableProperty]
    private bool _isAnimationPlaying;

    [ObservableProperty]
    private double _animationTimeSeconds;

    public bool HasModel => ModelGroup is not null;
    public bool HasPreviewOptions => PreviewOptions.Count > 0;
    public bool HasNoPreviewOptions => !HasPreviewOptions;
    // The selector is part of the character-preview workflow even when a mod omitted
    // customization metadata. Filtering remains slot-aware and becomes a no-op until
    // both forms are actually decoded, so unknown accessories can never disappear.
    public bool HasBodyShapeSwitch => GetArmorMeshes().Count > 0;
    public bool HasArmorSwitch => Armors.Count > 2;
    public bool HasAnimations => Animations.Count > 0;
    public double SelectedAnimationDuration => SelectedAnimation?.Option.Clip.LengthSeconds ?? 0;
    public string AnimationPlaybackGlyph => IsAnimationPlaying ? "\uE769" : "\uE768";
    public string AnimationPlaybackToolTip => _localizationService[
        IsAnimationPlaying ? "ModelPreviewPage.PauseAnimation" : "ModelPreviewPage.PlayAnimation"];
    public string AnimationTimeText => $"{AnimationTimeSeconds:0.00} / {SelectedAnimationDuration:0.00} s";
    public bool IsSlimBodySelected
    {
        get => !ShowStockyBody;
        set
        {
            if (value)
                ShowStockyBody = false;
        }
    }
    public bool IsStockyBodySelected
    {
        get => ShowStockyBody;
        set
        {
            if (value)
                ShowStockyBody = true;
        }
    }
    public int AutomaticallyHiddenMeshCount => _selection.HiddenMeshCount;
    public int VisibleMeshCount => GetBodyShapeMeshes().Count(_selection.VisibleMeshes.Contains);
    public string AutomaticallyHiddenMeshSummary => _localizationService["ModelPreviewPage.AutomaticallyHiddenSummary"]
        .Replace("{count}", AutomaticallyHiddenMeshCount.ToString());

    public ModelPreviewPageViewModel(
        ILogger<ModelPreviewPageViewModel> logger,
        IServiceProvider provider,
        ModService modService,
        PatchResourceInspectionService inspectionService,
        ModelPreviewBackend previewBackend,
        GpuSkinningService gpuSkinningService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navigationStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _inspectionService = inspectionService;
        _previewBackend = previewBackend;
        _gpuSkinningService = gpuSkinningService;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += LocalizationServiceOnPropertyChanged;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _animationTimer.Tick += AnimationTimerOnTick;

        _ = RefreshModsAsync();
    }

    public void SetInitialMod(ModData mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        _preferredMod = mod;

        var existingIndex = Mods
            .Select((existingMod, index) => new { existingMod, index })
            .FirstOrDefault(item => item.existingMod.Manifest.Guid == mod.Manifest.Guid)
            ?.index;
        if (existingIndex is int index)
            Mods[index] = mod;
        else
            Mods.Add(mod);

        SelectedMod = mod;
    }

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

    partial void OnModelGroupChanged(Model3DGroup? value) => OnPropertyChanged(nameof(HasModel));

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

    [RelayCommand]
    private void ToggleAnimationPlayback()
    {
        if (SelectedAnimation is null || SelectedAnimationDuration <= 0)
            return;

        if (IsAnimationPlaying)
        {
            StopAnimationPlayback();
            return;
        }

        IsAnimationPlaying = true;
        _isAnimationApplied = true;
        QueueAnimationFrame();
        _animationClock.Restart();
        _animationTimer.Start();
    }

    [RelayCommand]
    private void ResetAnimation()
    {
        StopAnimationPlayback();
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
        QueueRebuild(resetCamera: false);
    }

    private void AnimationTimerOnTick(object? sender, EventArgs e)
    {
        if (!IsAnimationPlaying || SelectedAnimationDuration <= 0 || Volatile.Read(ref _isDisposed) != 0)
            return;

        var elapsed = _animationClock.Elapsed.TotalSeconds;
        _animationClock.Restart();
        var next = AnimationTimeSeconds + elapsed;
        AnimationTimeSeconds = next >= SelectedAnimationDuration
            ? next % SelectedAnimationDuration
            : next;
    }

    private void StopAnimationPlayback()
    {
        _animationTimer.Stop();
        _animationClock.Reset();
        IsAnimationPlaying = false;
    }

    partial void OnUseAutomaticMaterialsChanged(bool value)
    {
        if (!value && SelectedTexture is not null)
            _ = LoadSelectedTextureAsync(SelectedTexture);
        else
            QueueRebuild();
    }

    partial void OnUseOriginalTextureResolutionChanged(bool value)
    {
        // 切换原始分辨率时，如果当前处于自动材质模式，需要重新加载所有自动匹配的多张贴图
        // （而不是只重新加载手动选中的单张贴图），因为多贴图模型的 BaseColor/Emissive
        // 等多张自动匹配图都要改用原分辨率解码，否则整个合成材质的清晰度不会提升。
        // 单张手动贴图预览仍然按旧逻辑重载以保留 SelectedTexturePreview 大图显示。
        SelectedTexturePreview = null;

        var mod = SelectedMod;
        if (mod is not null && UseAutomaticMaterials)
        {
            // 只清纹理预览缓存，不清几何/网格结果；重新加载自动匹配的多张贴图后重建模型组。
            Interlocked.Increment(ref _textureLoadGeneration);
            CancelActiveTextureLoad();
            _texturePreviews.Clear();
            _automaticTexturePreviewIds.Clear();
            _selectedOriginalTextureId = null;
            _selectedOriginalTexturePreview = null;
            _ = ReloadAutomaticTexturesAfterResolutionSwitchAsync(mod);
        }
        else if (SelectedTexture is not null)
        {
            _ = LoadSelectedTextureAsync(SelectedTexture);
            QueueRebuild();
        }
        else
        {
            QueueRebuild();
        }
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

    internal static IReadOnlyList<ulong> SelectAutomaticTextureIds(
        IReadOnlyList<ModelPreviewMesh> meshes,
        int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        var candidates = new List<ulong>();
        var priorities = new Dictionary<ulong, int>();

        static void AddCandidate(
            ICollection<ulong> target,
            IDictionary<ulong, int> priorities,
            ulong textureId,
            int priority)
        {
            if (textureId == 0)
                return;

            if (priorities.TryGetValue(textureId, out var existingPriority))
            {
                priorities[textureId] = Math.Min(existingPriority, priority);
                return;
            }

            priorities[textureId] = priority;
            target.Add(textureId);
        }

        foreach (var mesh in meshes)
        {
            var hasColorBinding = false;
            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor))
            {
                AddCandidate(candidates, priorities, textureId, priority: 0);
                hasColorBinding = true;
            }

            if (mesh.ColorTextureId is ulong colorTextureId)
            {
                AddCandidate(candidates, priorities, colorTextureId, priority: 0);
                hasColorBinding = true;
            }

            foreach (var textureId in mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                AddCandidate(candidates, priorities, textureId, priority: 1);

            // Older material layouts do not expose semantic bindings. Keep their
            // texture list as a bounded fallback, but do not preload known normal/mask maps.
            if (!hasColorBinding)
            {
                foreach (var textureId in mesh.TextureIds)
                    AddCandidate(candidates, priorities, textureId, priority: 2);
            }
        }

        return candidates
            .OrderBy(textureId => priorities[textureId])
            .Take(maximumCount)
            .ToArray();
    }

    private bool IsCurrentLoad(ModData mod, int generation) =>
        ReferenceEquals(mod, SelectedMod) && generation == _loadGeneration;

    private TextureInspectionItem? ChoosePreferredTexture()
    {
        var preferredColorTextureIds = Meshes
            .Select(static mesh => mesh.ColorTextureId)
            .Where(static textureId => textureId.HasValue)
            .Select(static textureId => textureId!.Value)
            .ToHashSet();
        var referencedTextureIds = Meshes
            .SelectMany(static mesh => mesh.MaterialTextures.AllTextureIds.Concat(mesh.TextureIds))
            .ToHashSet();
        return Textures
            .Where(texture => referencedTextureIds.Contains(texture.TextureId))
            .OrderBy(texture => preferredColorTextureIds.Contains(texture.TextureId) ? 0 :
                texture.PreviewRole == TexturePreviewRole.ColorCandidate ? 1 :
                texture.PreviewRole == TexturePreviewRole.Unknown ? 2 : 3)
            .ThenByDescending(static texture => (long)texture.Width * texture.Height)
            .FirstOrDefault()
            ?? Textures.FirstOrDefault();
    }

    private IReadOnlyList<ModelPreviewMesh> GetVisibleMeshes()
    {
        var bodyShapeMeshes = GetBodyShapeMeshes();
        var bodyShapeMeshSet = bodyShapeMeshes.ToHashSet();
        var selection = new ModelPreviewSelection(
            _selection.VisibleMeshes.Where(bodyShapeMeshSet.Contains).ToArray(),
            _selection.HiddenMeshCount);
        var selectedMesh = SelectedMesh is not null && bodyShapeMeshSet.Contains(SelectedMesh)
            ? SelectedMesh
            : null;
        return ModelPreviewMeshSelector.GetRenderMeshes(
            selection,
            bodyShapeMeshes,
            selectedMesh,
            IsolateSelectedMesh,
            ShowFilteredMeshes);
    }

    private IReadOnlyList<ModelPreviewMesh> GetBodyShapeMeshes()
    {
        var armorMeshes = GetArmorMeshes();
        var renderableArmorMeshes = _selection.VisibleMeshes.Where(armorMeshes.Contains).ToArray();
        return ModelPreviewBodyShapeSelection.Filter(armorMeshes, renderableArmorMeshes, ShowStockyBody);
    }

    private IReadOnlyList<ModelPreviewMesh> GetArmorMeshes() =>
        ModelPreviewBackend.FilterByArmor(Meshes, SelectedArmor?.Id);

    private async Task LoadAutomaticTexturePreviewsAsync(
        ModData mod,
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyList<TextureInspectionItem> textures,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        // 原始分辨率模式下，自动匹配的多张贴图也使用原分辨率解码，不再只对手动选中的单张贴图生效。
        // 原始分辨率上限（4K）远高于普通预览（1K），为避免同时解码 16 张大图把 LOH 撑爆，
        // 原始分辨率模式下降低自动并发解码上限，并减小单批次的最大数量。
        bool useOriginalResolution = UseOriginalTextureResolution;
        int maxPixelCount = useOriginalResolution
            ? ModelPreviewOriginalTexturePixels
            : ModelPreviewMaxTexturePixels;
        int maxPreviewCount = useOriginalResolution
            ? Math.Min(MaxAutomaticTexturePreviews, 8) // 4K × 8 张 ≈ 512 MiB 峰值 BGRA，保持可接受
            : MaxAutomaticTexturePreviews;
        int concurrency = useOriginalResolution ? 1 : 2;

        var textureMap = ModelPreviewTextureIndex.Create(textures);
        // WPF composes BaseColor and Emissive only. Normal and mask maps stay available
        // through the texture list, but pre-decoding them consumes memory without
        // affecting the rendered model.
        var referencedIds = SelectAutomaticTextureIds(meshes, maxPreviewCount)
            .Where(textureMap.ContainsKey)
            .ToArray();

        // The gate is disposed explicitly after every decode task has finished. Task.WhenAll
        // returns early on cancellation while sibling tasks are still unwinding; disposing
        // the gate too early would make their Release() throw ObjectDisposedException and
        // leave an unobserved faulted task behind.
        var decodeGate = new SemaphoreSlim(concurrency, concurrency);
        var decodeTasks = referencedIds.Select(async textureId =>
        {
            if (!textureMap.TryGetValue(textureId, out var texture))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateTextureCacheKey(texture, useOriginalResolution);
            if (_decodedTexturePreviews.TryGetValue(cacheKey, out var cached))
                return new LoadedTextureResult(textureId, texture, cached);

            try
            {
                await decodeGate.WaitAsync(cancellationToken);
                try
                {
                    var preview = await _inspectionService.PreviewTextureAsync(
                        mod.Directory, texture, maxPixelCount, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentLoad(mod, loadGeneration))
                        return null;

                    var bitmap = CreateModelBitmapSource(preview, useOriginalResolution);
                    if (bitmap is null || preview is null)
                        return null;

                    var role = ModelPreviewTextureAnalysis.Classify(preview);
                    var loaded = new LoadedTexturePreview(
                        bitmap,
                        role,
                        (long)texture.Width * texture.Height);
                    return new LoadedTextureResult(textureId, texture, loaded);
                }
                finally
                {
                    decodeGate.Release();
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                    throw;
                _logger.LogDebug(ex, "Texture {TextureId} could not be loaded for model preview", texture.TextureIdText);
                return null;
            }
        }).ToArray();

        try
        {
            foreach (var loaded in await Task.WhenAll(decodeTasks))
            {
                if (loaded is null)
                    continue;
                loaded.Texture.PreviewRole = loaded.Preview.Role;
                loaded.Texture.PreviewRoleText = GetTexturePreviewRoleText(loaded.Preview.Role);
                _texturePreviews[loaded.TextureId] = loaded.Preview;
                _automaticTexturePreviewIds.Add(loaded.TextureId);
                CacheDecodedTexture(CreateTextureCacheKey(loaded.Texture, useOriginalResolution), loaded.Preview);
            }
        }
        catch
        {
            // WhenAll returns early on cancellation, but sibling tasks may still be running.
            // Wait for all of them to settle before disposing the gate so a late Release()
            // cannot throw ObjectDisposedException and surface as an unobserved fault.
            try
            {
                await Task.WhenAll(decodeTasks);
            }
            catch
            {
                // The original exception is rethrown below; this wait only drains the tasks.
            }
            throw;
        }
        finally
        {
            decodeGate.Dispose();
        }
    }

    private async Task LoadSelectedTextureAsync(TextureInspectionItem? texture)
    {
        SelectedTexturePreview = null;
        var mod = SelectedMod;
        if (texture is null || mod is null)
            return;

        var useOriginalResolution = UseOriginalTextureResolution;
        var textureGeneration = Volatile.Read(ref _textureLoadGeneration);
        var cancellation = BeginTextureLoad();
        var cancellationToken = cancellation.Token;
        try
        {
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation))
                return;

            if (ModelPreviewTextureResolutionState.IsCurrentOriginalPreview(
                    useOriginalResolution,
                    texture.TextureId,
                    _selectedOriginalTextureId) &&
                _selectedOriginalTexturePreview is not null)
            {
                SelectedTexturePreview = _selectedOriginalTexturePreview.Image;
                await RebuildModelGroupAsync();
                return;
            }

            if (!useOriginalResolution &&
                (_texturePreviews.TryGetValue(texture.TextureId, out var cachedPreview) ||
                 TryGetDecodedTexture(texture, useOriginalResolution, out cachedPreview)))
            {
                SelectedTexturePreview = cachedPreview.Image;
                StoreManualTexturePreview(texture.TextureId, cachedPreview);
                await RebuildModelGroupAsync();
                return;
            }

            var preview = await _inspectionService.PreviewTextureAsync(
                mod.Directory,
                texture,
                useOriginalResolution ? ModelPreviewOriginalTexturePixels : ModelPreviewMaxTexturePixels,
                cancellationToken);
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation) ||
                useOriginalResolution != UseOriginalTextureResolution)
                return;

            if (preview is null)
                return;

            var role = ModelPreviewTextureAnalysis.Classify(preview);
            var bitmap = CreateModelBitmapSource(preview, useOriginalResolution);
            // The source-resolution decoder can have a 256 MiB managed BGRA buffer.
            // The frozen BitmapSource owns the pixel content needed by WPF; do not let
            // the async state machine retain the decoder result while rebuilding.
            preview = null;
            if (!IsCurrentTextureRequest(texture, mod, textureGeneration, cancellation))
                return;
            if (bitmap is not null)
            {
                texture.PreviewRole = role;
                texture.PreviewRoleText = GetTexturePreviewRoleText(role);
                SelectedTexturePreview = bitmap;
                var loaded = new LoadedTexturePreview(
                    bitmap,
                    role,
                    (long)texture.Width * texture.Height);
                if (useOriginalResolution)
                {
                    // A source mip may be very large. Retain exactly one separately
                    // from the bounded normal-resolution automatic preview cache.
                    _selectedOriginalTextureId = texture.TextureId;
                    _selectedOriginalTexturePreview = loaded;
                }
                else
                {
                    StoreManualTexturePreview(texture.TextureId, loaded);
                    CacheDecodedTexture(CreateTextureCacheKey(texture, useOriginalResolution), loaded);
                }
                await RebuildModelGroupAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The model, selected texture, or page lifetime moved on while decoding.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Texture {TextureId} could not be loaded for manual model preview", texture.TextureIdText);
        }
        finally
        {
            Interlocked.CompareExchange(ref _textureLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Model albedo inputs often pack unrelated data into alpha. WPF's ImageBrush would
    /// otherwise interpret that channel as opacity and make an otherwise valid model
    /// disappear, so the 3D preview deliberately renders the RGB channels as opaque.
    /// </summary>
    internal static ImageSource? CreateModelBitmapSource(
        TexturePreviewData? preview,
        bool useOriginalResolution = false)
    {
        if (preview is null)
            return null;

        if (preview.BgraPixels is not null)
        {
            var bitmap = BitmapSource.Create(
                preview.Width,
                preview.Height,
                96,
                96,
                PixelFormats.Bgr32,
                null,
                preview.BgraPixels,
                preview.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        if (preview.EncodedImageBytes is null)
            return null;

        using var stream = new MemoryStream(preview.EncodedImageBytes, writable: false);
        var png = new BitmapImage();
        png.BeginInit();
        png.CacheOption = BitmapCacheOption.OnLoad;
        if (GetTextureDecodePixelWidth(preview.Width, useOriginalResolution) is int decodePixelWidth)
            png.DecodePixelWidth = decodePixelWidth;
        png.StreamSource = stream;
        png.EndInit();
        png.Freeze();
        var opaque = new FormatConvertedBitmap(png, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    internal static int? GetTextureDecodePixelWidth(int sourcePixelWidth, bool useOriginalResolution) =>
        useOriginalResolution ? null : Math.Min(Math.Max(sourcePixelWidth, 1), 2048);

    private void QueueAnimationFrame()
    {
        if (Volatile.Read(ref _isDisposed) != 0 || ModelGroup is null)
            return;

        Interlocked.Exchange(ref _animationFrameRequested, 1);
        if (Interlocked.CompareExchange(ref _animationFrameWorkerRunning, 1, 0) == 0)
            _ = RunQueuedAnimationFramesAsync();
    }

    private async Task RunQueuedAnimationFramesAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _animationFrameRequested, 0);
                await ApplyAnimationFrameAsync(_pageLifetimeCancellation.Token);
                if (IsAnimationPlaying && Volatile.Read(ref _animationFrameRequested) != 0)
                    await Task.Delay(10, _pageLifetimeCancellation.Token);
            }
            while (Volatile.Read(ref _animationFrameRequested) != 0);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to update the model preview animation frame");
        }
        finally
        {
            Interlocked.Exchange(ref _animationFrameWorkerRunning, 0);
            if (Volatile.Read(ref _animationFrameRequested) != 0 &&
                Volatile.Read(ref _isDisposed) == 0 &&
                Interlocked.CompareExchange(ref _animationFrameWorkerRunning, 1, 0) == 0)
            {
                _ = RunQueuedAnimationFramesAsync();
            }
        }
    }

    private async Task ApplyAnimationFrameAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isDisposed) != 0 ||
            !_isAnimationApplied ||
            SelectedAnimation is not { } selectedAnimation)
        {
            return;
        }

        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _isDisposed) != 0 ||
                !_isAnimationApplied ||
                !ReferenceEquals(SelectedAnimation, selectedAnimation))
            {
                return;
            }

            var renderGeneration = _renderGeneration;
            var animationTimeSeconds = (float)AnimationTimeSeconds;
            var meshes = GetVisibleMeshes()
                .Where(mesh =>
                    _liveMeshGeometries.ContainsKey(mesh) &&
                    mesh.Skinning is { } skinning &&
                    ModelPreviewAnimationCompatibility.IsCompatible(
                        skinning.Skeleton,
                        selectedAnimation.Library))
                .ToArray();
            if (meshes.Length == 0)
                return;

            if (!ReferenceEquals(_animationFrameCacheChoice, selectedAnimation) ||
                _animationFrameCacheRenderGeneration != renderGeneration)
            {
                ClearAnimationFrameCache();
                _animationFrameCacheChoice = selectedAnimation;
                _animationFrameCacheRenderGeneration = renderGeneration;
            }

            var sample = GetAnimationFrameSample(
                selectedAnimation.Option.Clip.LengthSeconds,
                animationTimeSeconds,
                meshes);
            if (!_animationFrameCache.TryGetValue(sample.FrameIndex, out var updates))
            {
                updates = await Task.Run(
                    () => BuildAnimationGeometryUpdates(
                        meshes,
                        selectedAnimation,
                        sample.TimeSeconds,
                        _animationBindings,
                        _gpuSkinningService,
                        cancellationToken),
                    cancellationToken);
                if (renderGeneration == _renderGeneration &&
                    ReferenceEquals(_animationFrameCacheChoice, selectedAnimation))
                {
                    _animationFrameCache[sample.FrameIndex] = updates;
                }
            }
            if (Volatile.Read(ref _isDisposed) != 0 ||
                renderGeneration != _renderGeneration ||
                !ReferenceEquals(SelectedAnimation, selectedAnimation))
            {
                return;
            }

            foreach (var update in updates)
            {
                if (!_liveMeshGeometries.TryGetValue(update.Mesh, out var geometry))
                    continue;
                geometry.Positions = update.Positions;
                if (update.Normals is not null)
                    geometry.Normals = update.Normals;
            }
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private static AnimationGeometryUpdate[] BuildAnimationGeometryUpdates(
        IReadOnlyList<ModelPreviewMesh> meshes,
        ModelPreviewAnimationChoice selectedAnimation,
        float animationTimeSeconds,
        ConcurrentDictionary<AnimationBindingCacheKey, ModelPreviewAnimationBinding> animationBindings,
        GpuSkinningService gpuSkinningService,
        CancellationToken cancellationToken)
    {
        var transformsBySkeleton = new Dictionary<ModelPreviewSkeleton, IReadOnlyList<System.Numerics.Matrix4x4>>();
        var updates = new AnimationGeometryUpdate[meshes.Count];
        for (var meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = meshes[meshIndex];
            var skinning = mesh.Skinning!;
            if (!transformsBySkeleton.TryGetValue(skinning.Skeleton, out var transforms))
            {
                var bindingKey = new AnimationBindingCacheKey(
                    skinning.Skeleton,
                    selectedAnimation.Library,
                    selectedAnimation.Option.Clip);
                transforms = animationBindings.GetOrAdd(
                        bindingKey,
                        static key => new ModelPreviewAnimationBinding(
                            key.Skeleton,
                            key.Library.BoneHashes,
                            key.Clip,
                            key.Library.BonesId))
                    .SampleSkinningTransforms(animationTimeSeconds);
                transformsBySkeleton[skinning.Skeleton] = transforms;
            }

            var skinnedPositions = gpuSkinningService.TrySkinPositions(
                mesh,
                transforms,
                cancellationToken,
                out var gpuPositions)
                ? gpuPositions
                : ModelPreviewCpuSkinner.Skin(mesh, transforms, skinNormals: false).Positions;
            var positions = CreatePoint3DCollection(skinnedPositions);
            Vector3DCollection? normals = null;
            updates[meshIndex] = new AnimationGeometryUpdate(mesh, positions, normals);
        }
        return updates;
    }

    private static AnimationFrameSample GetAnimationFrameSample(
        float durationSeconds,
        float timeSeconds,
        IReadOnlyList<ModelPreviewMesh> meshes)
    {
        var bytesPerFrame = meshes.Sum(static mesh => (long)mesh.VertexCount * 3 * sizeof(double));
        var memoryBound = bytesPerFrame <= 0
            ? 1
            : (int)Math.Clamp(MaxAnimationFrameCacheBytes / bytesPerFrame, 1, MaxCachedAnimationFrames);
        var desiredFrames = durationSeconds > 0
            ? Math.Max(1, (int)Math.Ceiling(durationSeconds * AnimationFramesPerSecond))
            : 1;
        var frameCount = Math.Min(desiredFrames, memoryBound);
        var normalizedTime = durationSeconds > 0
            ? Math.Clamp(timeSeconds % durationSeconds, 0, durationSeconds)
            : 0;
        var frameIndex = durationSeconds > 0
            ? Math.Min((int)(normalizedTime / durationSeconds * frameCount), frameCount - 1)
            : 0;
        var sampleTime = durationSeconds > 0
            ? frameIndex * durationSeconds / frameCount
            : 0;
        return new AnimationFrameSample(frameIndex, sampleTime);
    }

    private void ClearAnimationFrameCache()
    {
        _animationFrameCache.Clear();
        _animationFrameCacheChoice = null;
        _animationFrameCacheRenderGeneration = -1;
    }

    private static Point3DCollection CreatePoint3DCollection(IReadOnlyList<float> values)
    {
        var collection = new Point3DCollection(values.Count / 3);
        for (var index = 0; index < values.Count; index += 3)
            collection.Add(new Point3D(values[index], values[index + 1], values[index + 2]));
        collection.Freeze();
        return collection;
    }

    private static Vector3DCollection CreateVector3DCollection(IReadOnlyList<float> values)
    {
        var collection = new Vector3DCollection(values.Count / 3);
        for (var index = 0; index < values.Count; index += 3)
            collection.Add(new Vector3D(values[index], values[index + 1], values[index + 2]));
        collection.Freeze();
        return collection;
    }

    private void QueueRebuild(bool resetCamera = true)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        if (resetCamera)
            Interlocked.Exchange(ref _cameraResetRequested, 1);
        if (Interlocked.Exchange(ref _rebuildRequested, 1) == 0)
            _ = RunQueuedRebuildsAsync();
    }

    private async Task RunQueuedRebuildsAsync()
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _rebuildRequested, 0);
                var resetCamera = Interlocked.Exchange(ref _cameraResetRequested, 0) != 0;
                await RebuildModelGroupAsync(resetCamera: resetCamera);
            }
            while (Volatile.Read(ref _rebuildRequested) != 0);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebuildModelGroupAsync(
        CancellationToken cancellationToken = default,
        bool resetCamera = true)
    {
        if (Volatile.Read(ref _isDisposed) != 0)
            return;

        var renderGeneration = Interlocked.Increment(ref _renderGeneration);
        ClearAnimationFrameCache();
        var meshes = GetVisibleMeshes().ToArray();
        if (meshes.Length == 0)
        {
            _liveMeshGeometries.Clear();
            ModelGroup = null;
            SuggestedCameraDistance = 5;
            SuggestedCameraYaw = 0;
            if (resetCamera)
                CameraResetVersion++;
            return;
        }

        var previews = ModelPreviewTextureResolutionState.GetMaterialPreviews(
            _texturePreviews,
            UseOriginalTextureResolution,
            SelectedTexture?.TextureId,
            _selectedOriginalTextureId,
            _selectedOriginalTexturePreview);
        var useAutomaticMaterials = UseAutomaticMaterials;
        var selectedTextureId = SelectedTexturePreview is not null ? SelectedTexture?.TextureId : null;
        var selectedAnimation = _isAnimationApplied ? SelectedAnimation : null;
        var animationTimeSeconds = (float)AnimationTimeSeconds;
        try
        {
            await _rebuildGate.WaitAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            // The page is being disposed and has closed the rebuild gate; drop the request.
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _isDisposed) != 0)
                return;
            var build = await Task.Run(
                () => BuildModelGroup(
                    meshes,
                    previews,
                    useAutomaticMaterials,
                    selectedTextureId,
                     selectedAnimation,
                     animationTimeSeconds,
                     _animationBindings,
                     _geometryCache,
                     _gpuSkinningService,
                     cancellationToken),
                cancellationToken);
            if (Volatile.Read(ref _isDisposed) != 0 || renderGeneration != _renderGeneration)
                return;

            ModelGroup = CreateLiveModelGroup(build.Group, meshes, _liveMeshGeometries);
            if (resetCamera)
            {
                if (selectedAnimation is null)
                {
                    SuggestedCameraDistance = Math.Max(build.Radius * 3.0, 1.0);
                    SuggestedCameraYaw = build.FrontYaw;
                }
                CameraResetVersion++;
            }
        }
        finally
        {
            try
            {
                _rebuildGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Page disposal may close the gate while a rebuild still holds it.
            }
        }
    }

    private static ModelPreviewBuildResult BuildModelGroup(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews,
        bool useAutomaticMaterials,
        ulong? selectedTextureId,
        ModelPreviewAnimationChoice? selectedAnimation,
        float animationTimeSeconds,
        ConcurrentDictionary<AnimationBindingCacheKey, ModelPreviewAnimationBinding> animationBindings,
        ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry> geometryCache,
        GpuSkinningService gpuSkinningService,
        CancellationToken cancellationToken)
    {
        var group = new Model3DGroup();
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        var skinningTransforms = new Dictionary<ModelPreviewSkeleton, IReadOnlyList<System.Numerics.Matrix4x4>>();

        foreach (var source in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CachedMeshGeometry cachedGeometry;
            if (selectedAnimation is not null && source.Skinning is { } skinning &&
                ModelPreviewAnimationCompatibility.IsCompatible(
                    skinning.Skeleton,
                    selectedAnimation.Library))
            {
                if (!skinningTransforms.TryGetValue(skinning.Skeleton, out var transforms))
                {
                    var bindingKey = new AnimationBindingCacheKey(
                        skinning.Skeleton,
                        selectedAnimation.Library,
                        selectedAnimation.Option.Clip);
                    transforms = animationBindings.GetOrAdd(
                            bindingKey,
                            static key => new ModelPreviewAnimationBinding(
                                key.Skeleton,
                                key.Library.BoneHashes,
                                key.Clip,
                                key.Library.BonesId))
                        .SampleSkinningTransforms(animationTimeSeconds);
                    skinningTransforms[skinning.Skeleton] = transforms;
                }

                var animatedPositions = gpuSkinningService.TrySkinPositions(
                    source,
                    transforms,
                    cancellationToken,
                    out var gpuPositions)
                    ? gpuPositions
                    : ModelPreviewCpuSkinner.Skin(source, transforms, skinNormals: false).Positions;
                cachedGeometry = CreateCachedMeshGeometry(
                    source,
                    animatedPositions,
                    source.Normals);
            }
            else
            {
                cachedGeometry = geometryCache.GetValue(source, static mesh => CreateCachedMeshGeometry(mesh));
            }
            var geometry = cachedGeometry.Geometry;
            minX = Math.Min(minX, cachedGeometry.MinX);
            minY = Math.Min(minY, cachedGeometry.MinY);
            minZ = Math.Min(minZ, cachedGeometry.MinZ);
            maxX = Math.Max(maxX, cachedGeometry.MaxX);
            maxY = Math.Max(maxY, cachedGeometry.MaxY);
            maxZ = Math.Max(maxZ, cachedGeometry.MaxZ);
            var materialKey = CreateMaterialKey(source, useAutomaticMaterials, selectedTextureId, texturePreviews);
            if (!materials.TryGetValue(materialKey, out var material))
            {
                material = CreateMaterial(source, texturePreviews, useAutomaticMaterials, selectedTextureId);
                materials[materialKey] = material;
            }

            var model = new GeometryModel3D(geometry, material) { BackMaterial = material };
            model.Freeze();
            group.Children.Add(model);
        }

        var center = new Vector3D(
            (minX + maxX) / 2,
            (minY + maxY) / 2,
            (minZ + maxZ) / 2);
        var radius = Math.Max(
            Math.Sqrt(
                Math.Pow(maxX - minX, 2) +
                Math.Pow(maxY - minY, 2) +
                Math.Pow(maxZ - minZ, 2)) / 2,
            0.5);
        var presentationRotation = ModelPreviewCharacterOrientation.GetRequiredRotation(meshes);
        group.Transform = CreatePresentationTransform(center, presentationRotation);
        group.Transform.Freeze();
        group.Freeze();
        return new ModelPreviewBuildResult(
            group,
            radius,
            ModelPreviewCharacterOrientation.GetSuggestedFrontYaw(presentationRotation));
    }

    private static Model3DGroup CreateLiveModelGroup(
        Model3DGroup frozenGroup,
        IReadOnlyList<ModelPreviewMesh> meshes,
        Dictionary<ModelPreviewMesh, MeshGeometry3D> liveGeometries)
    {
        liveGeometries.Clear();
        var group = new Model3DGroup { Transform = frozenGroup.Transform };
        for (var index = 0; index < frozenGroup.Children.Count; index++)
        {
            if (index >= meshes.Count ||
                frozenGroup.Children[index] is not GeometryModel3D sourceModel ||
                sourceModel.Geometry is not MeshGeometry3D sourceGeometry)
            {
                group.Children.Add(frozenGroup.Children[index]);
                continue;
            }

            var liveGeometry = new MeshGeometry3D
            {
                Positions = sourceGeometry.Positions,
                Normals = sourceGeometry.Normals,
                TextureCoordinates = sourceGeometry.TextureCoordinates,
                TriangleIndices = sourceGeometry.TriangleIndices
            };
            var liveModel = new GeometryModel3D(liveGeometry, sourceModel.Material)
            {
                BackMaterial = sourceModel.BackMaterial,
                Transform = sourceModel.Transform
            };
            group.Children.Add(liveModel);
            liveGeometries[meshes[index]] = liveGeometry;
        }
        return group;
    }

    internal static Transform3D CreatePresentationTransform(
        Vector3D center,
        ModelPreviewPresentationRotation rotation)
    {
        if (rotation == ModelPreviewPresentationRotation.None)
            return new TranslateTransform3D(-center.X, -center.Y, -center.Z);

        var (axis, angle) = rotation switch
        {
            ModelPreviewPresentationRotation.PositiveXToPositiveY => (new Vector3D(0, 0, 1), 90d),
            ModelPreviewPresentationRotation.NegativeXToPositiveY => (new Vector3D(0, 0, 1), -90d),
            ModelPreviewPresentationRotation.PositiveZToPositiveY => (new Vector3D(1, 0, 0), -90d),
            ModelPreviewPresentationRotation.NegativeZToPositiveY => (new Vector3D(1, 0, 0), 90d),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, null)
        };
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-center.X, -center.Y, -center.Z));
        transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(axis, angle)));
        return transform;
    }

    private static CachedMeshGeometry CreateCachedMeshGeometry(
        ModelPreviewMesh source,
        float[]? positionOverride = null,
        float[]? normalOverride = null)
    {
        var positions = positionOverride is { Length: > 0 } && positionOverride.Length == source.Positions.Length
            ? positionOverride
            : source.Positions;
        var normals = normalOverride is { Length: > 0 } && normalOverride.Length == source.Positions.Length
            ? normalOverride
            : source.Normals;
        var hasNormals = normals is { Length: > 0 } && normals.Length == source.Positions.Length;
        var hasCoordinates = source.TextureCoordinates is { Length: > 0 } &&
                             source.TextureCoordinates.Length == source.VertexCount * 2;
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(source.VertexCount),
            Normals = new Vector3DCollection(hasNormals ? source.VertexCount : 0),
            TextureCoordinates = new PointCollection(hasCoordinates ? source.VertexCount : 0),
            TriangleIndices = new Int32Collection(source.TriangleIndices.Length)
        };

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        for (var index = 0; index < positions.Length; index += 3)
        {
            var x = positions[index];
            var y = positions[index + 1];
            var z = positions[index + 2];
            geometry.Positions.Add(new Point3D(x, y, z));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        if (hasNormals && normals is not null)
            for (var index = 0; index < normals.Length; index += 3)
                geometry.Normals.Add(new Vector3D(normals[index], normals[index + 1], normals[index + 2]));

        if (hasCoordinates && source.TextureCoordinates is { } coordinates)
            for (var index = 0; index < coordinates.Length; index += 2)
                geometry.TextureCoordinates.Add(new System.Windows.Point(coordinates[index], coordinates[index + 1]));

        foreach (var index in source.TriangleIndices)
            geometry.TriangleIndices.Add(index);

        geometry.Freeze();
        return new CachedMeshGeometry(geometry, minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static TexturePreviewCacheKey CreateTextureCacheKey(
        TextureInspectionItem texture,
        bool useOriginalResolution) => new(
        texture.PatchPath,
        texture.TextureId,
        texture.PayloadSource,
        texture.PayloadKind,
        texture.MainOffset,
        texture.GpuOffset,
        texture.StreamOffset,
        texture.GpuSize,
        texture.StreamSize,
        texture.Width,
        texture.Height,
        texture.MipCount,
        texture.DxgiFormat,
        useOriginalResolution);

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

    private void CacheModelResult(string key, ModelPreviewResult result)
    {
        if (_modelResultCache.ContainsKey(key))
            return;
        while (_modelResultCache.Count >= MaxModelResultCacheEntries && _modelResultOrder.TryDequeue(out var oldest))
            _modelResultCache.Remove(oldest);
        _modelResultCache[key] = result;
        _modelResultOrder.Enqueue(key);
    }

    private void ClearActiveTexturePreviews()
    {
        Interlocked.Increment(ref _textureLoadGeneration);
        CancelActiveTextureLoad();
        _texturePreviews.Clear();
        _automaticTexturePreviewIds.Clear();
        _selectedOriginalTextureId = null;
        _selectedOriginalTexturePreview = null;
    }

    private CancellationTokenSource BeginTextureLoad()
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pageLifetimeCancellation.Token,
            _loadCancellation?.Token ?? CancellationToken.None);
        Interlocked.Exchange(ref _textureLoadCancellation, cancellation)?.Cancel();
        return cancellation;
    }

    private void CancelActiveTextureLoad() =>
        Interlocked.Exchange(ref _textureLoadCancellation, null)?.Cancel();

    private bool IsCurrentTextureRequest(
        TextureInspectionItem texture,
        ModData mod,
        int textureGeneration,
        CancellationTokenSource cancellation) =>
        ModelPreviewTextureRequestState.IsCurrent(
            textureGeneration,
            Volatile.Read(ref _textureLoadGeneration),
            cancellation.IsCancellationRequested) &&
        Volatile.Read(ref _isDisposed) == 0 &&
        ReferenceEquals(cancellation, _textureLoadCancellation) &&
        ReferenceEquals(texture, SelectedTexture) &&
        ReferenceEquals(mod, SelectedMod);

    private void ClearRetainedPreviewCaches()
    {
        _decodedTexturePreviews.Clear();
        _decodedTextureOrder.Clear();
        _modelResultCache.Clear();
        _modelResultOrder.Clear();
        // ConditionalWeakTable has no Clear API. Replacing it removes this page's
        // strong cache root so old WPF MeshGeometry3D instances can be collected.
        _geometryCache = new ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry>();
        _animationBindings.Clear();
        _gpuSkinningService.ReleaseMeshes(Meshes);
        _liveMeshGeometries.Clear();
        ClearAnimationFrameCache();
    }

    private void StoreManualTexturePreview(ulong textureId, LoadedTexturePreview preview)
    {
        _texturePreviews[textureId] = preview;
        while (_texturePreviews.Count > MaxActiveTexturePreviewEntries)
        {
            var evictionCandidate = _texturePreviews.Keys
                .Where(id => !_automaticTexturePreviewIds.Contains(id) && id != SelectedTexture?.TextureId)
                .Select(static id => (ulong?)id)
                .FirstOrDefault();
            if (evictionCandidate is null)
                return;
            _texturePreviews.Remove(evictionCandidate.Value);
        }
    }

    private bool TryGetDecodedTexture(
        TextureInspectionItem texture,
        bool useOriginalResolution,
        out LoadedTexturePreview preview)
        => _decodedTexturePreviews.TryGetValue(
            CreateTextureCacheKey(texture, useOriginalResolution),
            out preview!);

    private void CacheDecodedTexture(TexturePreviewCacheKey key, LoadedTexturePreview preview)
    {
        if (_decodedTexturePreviews.ContainsKey(key))
            return;
        while (_decodedTexturePreviews.Count >= MaxDecodedTextureCacheEntries && _decodedTextureOrder.TryDequeue(out var oldest))
            _decodedTexturePreviews.Remove(oldest);
        _decodedTexturePreviews[key] = preview;
        _decodedTextureOrder.Enqueue(key);
    }

    internal static ulong? FindPreferredTextureId(
        ModelPreviewMesh mesh,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews)
    {
        var semanticColorId = mesh.MaterialTextures
            .Get(ModelPreviewTextureRole.BaseColor)
            .FirstOrDefault(texturePreviews.ContainsKey);
        if (semanticColorId != 0)
            return semanticColorId;

        if (mesh.ColorTextureId is ulong colorTextureId &&
            texturePreviews.ContainsKey(colorTextureId))
            return colorTextureId;

        return mesh.TextureIds
            .Where(texturePreviews.ContainsKey)
            .OrderBy(id => texturePreviews[id].Role == TexturePreviewRole.ColorCandidate ? 0 :
                texturePreviews[id].Role == TexturePreviewRole.Unknown ? 1 : 2)
            .ThenByDescending(id => texturePreviews[id].SourcePixelCount)
            .Cast<ulong?>()
            .FirstOrDefault();
    }

    internal static ulong? GetSelectedTextureIdForMesh(
        ModelPreviewMesh mesh,
        ulong? selectedTextureId)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return selectedTextureId is ulong textureId && mesh.TextureIds.Contains(textureId)
            ? textureId
            : null;
    }

    internal static Material CreateMaterial(ImageSource? image)
    {
        Brush brush = image is null
            ? new SolidColorBrush(Color.FromRgb(184, 193, 202))
            : new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                Viewbox = new Rect(0, 0, 1, 1),
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile
            };

        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    internal static Material CreateMaterial(
        ModelPreviewMesh mesh,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews,
        bool useAutomaticMaterials,
        ulong? selectedTextureId)
    {
        var textureIds = useAutomaticMaterials
            ? GetAutomaticMaterialTextureIds(mesh)
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : []);

        var semanticBaseColorIds = mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
            .Concat(mesh.ColorTextureId is ulong color ? [color] : [])
            .Distinct()
            .ToArray();
        var baseColor = !useAutomaticMaterials
            ? textureIds.FirstOrDefault(texturePreviews.ContainsKey)
            : semanticBaseColorIds.Length > 0
                ? semanticBaseColorIds.FirstOrDefault(texturePreviews.ContainsKey)
                : textureIds.FirstOrDefault(texturePreviews.ContainsKey);
        var baseImage = baseColor != 0 && texturePreviews.TryGetValue(baseColor, out var basePreview)
            ? basePreview.Image
            : null;

        if (baseImage is null)
            return CreateMaterial(null);

        var emissiveImages = useAutomaticMaterials
            ? mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive)
                .Where(texturePreviews.ContainsKey)
                .Select(textureId => texturePreviews[textureId].Image)
                .ToArray()
            : [];
        if (emissiveImages.Length == 0)
            return CreateMaterial(baseImage);

        // WPF's fixed-function 3D material has one Brush slot. Compose the material's
        // base color and emissive inputs into one DrawingBrush so every referenced
        // texture remains visible in the preview without pretending a normal map is an
        // albedo. The normal and mask inputs are still loaded and exposed in the asset
        // graph for future shader-backed rendering.
        var drawingGroup = new DrawingGroup();
        drawingGroup.Children.Add(new ImageDrawing(baseImage, new Rect(0, 0, 1, 1)));
        foreach (var emissiveImage in emissiveImages)
        {
            var emissiveGroup = new DrawingGroup { Opacity = 0.22 };
            emissiveGroup.Children.Add(new ImageDrawing(emissiveImage, new Rect(0, 0, 1, 1)));
            drawingGroup.Children.Add(emissiveGroup);
        }

        drawingGroup.Freeze();
        var brush = new DrawingBrush(drawingGroup)
        {
            Stretch = Stretch.Fill,
            Viewbox = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile
        };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    private static string CreateMaterialKey(
        ModelPreviewMesh mesh,
        bool useAutomaticMaterials,
        ulong? selectedTextureId,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews)
    {
        var ids = useAutomaticMaterials
            ? GetAutomaticMaterialTextureIds(mesh)
                .Where(texturePreviews.ContainsKey)
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : Enumerable.Empty<ulong>());
        return string.Join(",", ids.OrderBy(static id => id));
    }

    private static IReadOnlyList<ulong> GetAutomaticMaterialTextureIds(ModelPreviewMesh mesh)
    {
        var semanticBaseColorIds = mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
            .Concat(mesh.ColorTextureId is ulong color ? [color] : [])
            .Distinct()
            .ToArray();

        return semanticBaseColorIds.Length > 0
            ? semanticBaseColorIds
                .Concat(mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive))
                .Distinct()
                .ToArray()
            : mesh.MaterialTextures.Get(ModelPreviewTextureRole.Emissive)
                .Concat(mesh.TextureIds)
                .Distinct()
                .ToArray();
    }

    private void UpdateLocalizedPreviewLabels()
    {
        foreach (var mesh in Meshes)
        {
            mesh.PreviewStatusText = mesh.RenderStatus switch
            {
                ModelPreviewMeshRenderStatus.HiddenCullingBody => _localizationService["ModelPreviewPage.HiddenCullingBody"],
                ModelPreviewMeshRenderStatus.HiddenLargeOutlier => _localizationService["ModelPreviewPage.HiddenLargeOutlier"],
                ModelPreviewMeshRenderStatus.HiddenProxyGeometry => _localizationService["ModelPreviewPage.HiddenProxyGeometry"],
                ModelPreviewMeshRenderStatus.HiddenCollisionSphere => _localizationService["ModelPreviewPage.HiddenCollisionSphere"],
                _ => _localizationService["ModelPreviewPage.PreviewVisible"]
            };
            mesh.UvStatusText = mesh.HasTextureCoordinates
                ? _localizationService["ModelPreviewPage.HasUv"]
                : _localizationService["ModelPreviewPage.NoUv"];
        }

        foreach (var texture in Textures)
            texture.PreviewRoleText = GetTexturePreviewRoleText(texture.PreviewRole);

        foreach (var armor in Armors.Where(static armor => armor.IsAll))
            armor.Name = _localizationService["ModelPreviewPage.AllArmors"];
    }

    private string GetTexturePreviewRoleText(TexturePreviewRole role) => role switch
    {
        TexturePreviewRole.ColorCandidate => _localizationService["ModelPreviewPage.ColorCandidate"],
        TexturePreviewRole.LikelyNormalMap => _localizationService["ModelPreviewPage.LikelyNormalMap"],
        _ => _localizationService["ModelPreviewPage.UnclassifiedTexture"]
    };

    private void LocalizationServiceOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AutomaticallyHiddenMeshSummary));
        OnPropertyChanged(nameof(AnimationPlaybackToolTip));
        UpdateLocalizedPreviewLabels();
        UpdateCameraOrientationText(_cameraDirection);
    }

    internal void UpdateCameraOrientation(double yaw, double pitch)
    {
        _cameraDirection = ModelPreviewViewportGuides.GetCameraDirection(yaw, SuggestedCameraYaw, pitch);
        UpdateCameraOrientationText(_cameraDirection);
    }

    private void UpdateCameraOrientationText(ModelPreviewCameraDirection direction)
    {
        var directionKey = direction switch
        {
            ModelPreviewCameraDirection.Front => "ModelPreviewPage.OrientationFront",
            ModelPreviewCameraDirection.Right => "ModelPreviewPage.OrientationRight",
            ModelPreviewCameraDirection.Back => "ModelPreviewPage.OrientationBack",
            ModelPreviewCameraDirection.Left => "ModelPreviewPage.OrientationLeft",
            ModelPreviewCameraDirection.Top => "ModelPreviewPage.OrientationTop",
            ModelPreviewCameraDirection.Bottom => "ModelPreviewPage.OrientationBottom",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
        };
        CameraOrientationText = _localizationService["ModelPreviewPage.CameraOrientation"]
            .Replace("{direction}", _localizationService[directionKey]);
    }

    protected override void OnDispose()
    {
        Volatile.Write(ref _isDisposed, 1);
        StopAnimationPlayback();
        _animationTimer.Tick -= AnimationTimerOnTick;
        _pageLifetimeCancellation.Cancel();
        _loadCancellation?.Cancel();
        Interlocked.Increment(ref _renderGeneration);
        ModelGroup = null;
        _gpuSkinningService.ReleaseMeshes(Meshes);
        _liveMeshGeometries.Clear();
        ClearAnimationFrameCache();
        SelectedTexturePreview = null;
        ClearActiveTexturePreviews();
        ClearRetainedPreviewCaches();
        SelectedTexture = null;
        SelectedMesh = null;
        SelectedArmor = null;
        SelectedMod = null;
        Meshes.Clear();
        Textures.Clear();
        Armors.Clear();
        Animations.Clear();
        // A queued rebuild can still be unwinding after navigation. It observes
        // _isDisposed/_renderGeneration above; the rebuild gate and page-lifetime
        // cancellation are guarded/verified against post-dispose access, so both are
        // released here without surfacing a fault.
        _localizationService.PropertyChanged -= LocalizationServiceOnPropertyChanged;
        _pageLifetimeCancellation.Dispose();
        _rebuildGate.Dispose();
    }

    internal sealed record ModelPreviewAnimationChoice(
        ModelPreviewAnimationLibrary Library,
        ModelPreviewAnimationOption Option)
    {
        public string DisplayName => Option.DisplayName;
    }

    internal sealed record LoadedTexturePreview(
        ImageSource Image,
        TexturePreviewRole Role,
        long SourcePixelCount);
    private sealed record LoadedTextureResult(ulong TextureId, TextureInspectionItem Texture, LoadedTexturePreview Preview);
    private sealed record TexturePreviewCacheKey(
        string PatchPath,
        ulong TextureId,
        string PayloadSource,
        string PayloadKind,
        ulong MainOffset,
        ulong GpuOffset,
        ulong StreamOffset,
        uint GpuSize,
        uint StreamSize,
        int Width,
        int Height,
        int MipCount,
        int DxgiFormat,
        bool UseOriginalResolution);
    private sealed record CachedMeshGeometry(
        MeshGeometry3D Geometry,
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ);
    private sealed record AnimationBindingCacheKey(
        ModelPreviewSkeleton Skeleton,
        ModelPreviewAnimationLibrary Library,
        ModelPreviewAnimationClip Clip);
    private sealed record AnimationGeometryUpdate(
        ModelPreviewMesh Mesh,
        Point3DCollection Positions,
        Vector3DCollection? Normals);
    private readonly record struct AnimationFrameSample(int FrameIndex, float TimeSeconds);
    private sealed record ModelPreviewBuildResult(Model3DGroup Group, double Radius, double FrontYaw);
}
