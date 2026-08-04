using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class ModelPreviewPageViewModel : PageViewModelBase
{
    private const int ModelPreviewMaxTexturePixels = 1_048_576; // 1024 x 1024 is sufficient for the viewport.
    private const int MaxAutomaticTexturePreviews = 16;
    private const int MaxActiveTexturePreviewEntries = MaxAutomaticTexturePreviews + 1;
    private const int MaxDecodedTextureCacheEntries = 12;
    private const int MaxModelResultCacheEntries = 1;
    private readonly ILogger<ModelPreviewPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navigationStore;
    private readonly ModService _modService;
    private readonly PatchResourceInspectionService _inspectionService;
    private readonly ModelPreviewBackend _previewBackend;
    private readonly LocalizationService _localizationService;
    private readonly Dictionary<ulong, LoadedTexturePreview> _texturePreviews = [];
    private readonly HashSet<ulong> _automaticTexturePreviewIds = [];
    private readonly Dictionary<TexturePreviewCacheKey, LoadedTexturePreview> _decodedTexturePreviews = [];
    private readonly Queue<TexturePreviewCacheKey> _decodedTextureOrder = [];
    private readonly Dictionary<string, ModelPreviewResult> _modelResultCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _modelResultOrder = [];
    private ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry> _geometryCache = new();
    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private ModelPreviewSelection _selection = new([], 0);
    private Guid? _initialModGuid;
    private int _renderGeneration;
    private int _loadGeneration;
    private bool _selectingAutomaticTexture;
    private CancellationTokenSource? _loadCancellation;
    private int _rebuildRequested;

    public override string Title => _localizationService["ModelPreviewPage.Title"];

    public ObservableCollection<ModData> Mods { get; } = [];
    public ObservableCollection<ModelPreviewMesh> Meshes { get; } = [];
    public ObservableCollection<TextureInspectionItem> Textures { get; } = [];
    public ObservableCollection<ModelPreviewArmorOption> Armors { get; } = [];
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
    private bool _showStockyBody = true;

    public bool HasModel => ModelGroup is not null;
    public bool HasPreviewOptions => PreviewOptions.Count > 0;
    public bool HasNoPreviewOptions => !HasPreviewOptions;
    public bool HasBodyShapeSwitch => GetBodyShapeSwitchSlots().Count > 0;
    public bool HasArmorSwitch => Armors.Count > 2;
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
        LocalizationService localizationService)
    {
        _logger = logger;
        _navigationStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _inspectionService = inspectionService;
        _previewBackend = previewBackend;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += LocalizationServiceOnPropertyChanged;

        _ = RefreshModsAsync();
    }

    public void SetInitialMod(ModData mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        _initialModGuid = mod.Manifest.Guid;
        if (Mods.Contains(mod))
            SelectedMod = mod;
    }

    [RelayCommand]
    private void GoBack() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshMods() => await RefreshModsAsync();

    partial void OnSelectedModChanged(ModData? value)
    {
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

    partial void OnUseAutomaticMaterialsChanged(bool value)
    {
        if (!value && SelectedTexture is not null)
        {
            if (_texturePreviews.TryGetValue(SelectedTexture.TextureId, out var preview))
            {
                SelectedTexturePreview = preview.Image;
                QueueRebuild();
            }
            else
                _ = LoadSelectedTextureAsync(SelectedTexture);
        }
        else
            QueueRebuild();
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

    private async Task RefreshModsAsync()
    {
        if (!_modService.Initialized)
        {
            StatusText = _localizationService["ModelPreviewPage.NotReady"];
            return;
        }

        Mods.Clear();
        foreach (var mod in _modService.Mods.OrderBy(static mod => mod.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
            Mods.Add(mod);

        SelectedMod = Mods.FirstOrDefault(mod => mod.Manifest.Guid == _initialModGuid) ?? Mods.FirstOrDefault();
        if (SelectedMod is null)
            StatusText = _localizationService["ModelPreviewPage.EmptyMods"];
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
            Meshes.Clear();
            Textures.Clear();
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
            UseAutomaticMaterials = true;
            SelectedTexture = null;
            SelectedTexturePreview = null;
            SelectedArmor = null;
            ModelGroup = null;
            SuggestedCameraDistance = 5;
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
            SelectedArmor = Armors.FirstOrDefault(static armor => armor.IsAll) ?? Armors.FirstOrDefault();

            _selection = ModelPreviewMeshSelector.Select(Meshes);
            UpdateLocalizedPreviewLabels();
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshCount));
            OnPropertyChanged(nameof(VisibleMeshCount));
            OnPropertyChanged(nameof(AutomaticallyHiddenMeshSummary));
            OnPropertyChanged(nameof(HasBodyShapeSwitch));
            OnPropertyChanged(nameof(HasArmorSwitch));
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

    private IReadOnlySet<ModelPreviewCustomizationSlot> GetBodyShapeSwitchSlots()
    {
        var armorMeshes = GetArmorMeshes();
        return ModelPreviewBodyShapeSelection.GetSwitchableSlots(
            armorMeshes,
            _selection.VisibleMeshes.Where(armorMeshes.Contains).ToArray());
    }

    private async Task LoadAutomaticTexturePreviewsAsync(
        ModData mod,
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyList<TextureInspectionItem> textures,
        int loadGeneration,
        CancellationToken cancellationToken)
    {
        var textureMap = ModelPreviewTextureIndex.Create(textures);
        // WPF composes BaseColor and Emissive only. Normal and mask maps stay available
        // through the texture list, but pre-decoding them consumes memory without
        // affecting the rendered model.
        var referencedIds = SelectAutomaticTextureIds(meshes, MaxAutomaticTexturePreviews)
            .Where(textureMap.ContainsKey)
            .ToArray();

        using var decodeGate = new SemaphoreSlim(2, 2);
        var decodeTasks = referencedIds.Select(async textureId =>
        {
            if (!textureMap.TryGetValue(textureId, out var texture))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateTextureCacheKey(texture);
            if (_decodedTexturePreviews.TryGetValue(cacheKey, out var cached))
                return new LoadedTextureResult(textureId, texture, cached);

            try
            {
                await decodeGate.WaitAsync(cancellationToken);
                try
                {
                    var preview = await _inspectionService.PreviewTextureAsync(
                        mod.Directory, texture, ModelPreviewMaxTexturePixels, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentLoad(mod, loadGeneration))
                        return null;

                    var bitmap = CreateModelBitmapSource(preview);
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

        foreach (var loaded in await Task.WhenAll(decodeTasks))
        {
            if (loaded is null)
                continue;
            loaded.Texture.PreviewRole = loaded.Preview.Role;
            loaded.Texture.PreviewRoleText = GetTexturePreviewRoleText(loaded.Preview.Role);
            _texturePreviews[loaded.TextureId] = loaded.Preview;
            _automaticTexturePreviewIds.Add(loaded.TextureId);
            CacheDecodedTexture(CreateTextureCacheKey(loaded.Texture), loaded.Preview);
        }
    }

    private async Task LoadSelectedTextureAsync(TextureInspectionItem? texture)
    {
        SelectedTexturePreview = null;
        var mod = SelectedMod;
        if (texture is null || mod is null)
            return;

        try
        {
            if (!ReferenceEquals(texture, SelectedTexture) || !ReferenceEquals(mod, SelectedMod))
                return;

            if (_texturePreviews.TryGetValue(texture.TextureId, out var cachedPreview) ||
                TryGetDecodedTexture(texture, out cachedPreview))
            {
                SelectedTexturePreview = cachedPreview.Image;
                StoreManualTexturePreview(texture.TextureId, cachedPreview);
                await RebuildModelGroupAsync();
                return;
            }

            var preview = await _inspectionService.PreviewTextureAsync(
                mod.Directory, texture, ModelPreviewMaxTexturePixels, _loadCancellation?.Token ?? CancellationToken.None);
            if (!ReferenceEquals(texture, SelectedTexture) || !ReferenceEquals(mod, SelectedMod))
                return;

            var bitmap = CreateModelBitmapSource(preview);
            if (bitmap is not null && preview is not null)
            {
                var role = ModelPreviewTextureAnalysis.Classify(preview);
                texture.PreviewRole = role;
                texture.PreviewRoleText = GetTexturePreviewRoleText(role);
                SelectedTexturePreview = bitmap;
                var loaded = new LoadedTexturePreview(
                    bitmap,
                    role,
                    (long)texture.Width * texture.Height);
                StoreManualTexturePreview(texture.TextureId, loaded);
                CacheDecodedTexture(CreateTextureCacheKey(texture), loaded);
                await RebuildModelGroupAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Texture {TextureId} could not be loaded for manual model preview", texture.TextureIdText);
        }
    }

    /// <summary>
    /// Model albedo inputs often pack unrelated data into alpha. WPF's ImageBrush would
    /// otherwise interpret that channel as opacity and make an otherwise valid model
    /// disappear, so the 3D preview deliberately renders the RGB channels as opaque.
    /// </summary>
    internal static ImageSource? CreateModelBitmapSource(TexturePreviewData? preview)
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
        png.DecodePixelWidth = Math.Min(Math.Max(preview.Width, 1), 2048);
        png.StreamSource = stream;
        png.EndInit();
        png.Freeze();
        var opaque = new FormatConvertedBitmap(png, PixelFormats.Bgr32, null, 0);
        opaque.Freeze();
        return opaque;
    }

    private void QueueRebuild()
    {
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
                await RebuildModelGroupAsync();
            }
            while (Volatile.Read(ref _rebuildRequested) != 0);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RebuildModelGroupAsync(CancellationToken cancellationToken = default)
    {
        var renderGeneration = Interlocked.Increment(ref _renderGeneration);
        var meshes = GetVisibleMeshes().ToArray();
        if (meshes.Length == 0)
        {
            ModelGroup = null;
            SuggestedCameraDistance = 5;
            return;
        }

        var previews = _texturePreviews.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var useAutomaticMaterials = UseAutomaticMaterials;
        var selectedTextureId = SelectedTexturePreview is not null ? SelectedTexture?.TextureId : null;
        await _rebuildGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var build = await Task.Run(
                () => BuildModelGroup(meshes, previews, useAutomaticMaterials, selectedTextureId, _geometryCache),
                cancellationToken);
            if (renderGeneration != _renderGeneration)
                return;

            ModelGroup = build.Group;
            SuggestedCameraDistance = Math.Max(build.Radius * 3.0, 1.0);
        }
        finally
        {
            _rebuildGate.Release();
        }
    }

    private static ModelPreviewBuildResult BuildModelGroup(
        IReadOnlyList<ModelPreviewMesh> meshes,
        IReadOnlyDictionary<ulong, LoadedTexturePreview> texturePreviews,
        bool useAutomaticMaterials,
        ulong? selectedTextureId,
        ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry> geometryCache)
    {
        var group = new Model3DGroup();
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;

        foreach (var source in meshes)
        {
            var cachedGeometry = geometryCache.GetValue(source, static mesh => CreateCachedMeshGeometry(mesh));
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
        group.Transform = CreatePresentationTransform(
            center,
            ModelPreviewCharacterOrientation.GetRequiredRotation(meshes));
        group.Transform.Freeze();
        group.Freeze();
        return new ModelPreviewBuildResult(group, radius);
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

    private static CachedMeshGeometry CreateCachedMeshGeometry(ModelPreviewMesh source)
    {
        var hasNormals = source.Normals is { Length: > 0 } && source.Normals.Length == source.Positions.Length;
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
        for (var index = 0; index < source.Positions.Length; index += 3)
        {
            var x = source.Positions[index];
            var y = source.Positions[index + 1];
            var z = source.Positions[index + 2];
            geometry.Positions.Add(new Point3D(x, y, z));
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        if (hasNormals && source.Normals is { } normals)
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

    private static TexturePreviewCacheKey CreateTextureCacheKey(TextureInspectionItem texture) => new(
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
        texture.DxgiFormat);

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
        _texturePreviews.Clear();
        _automaticTexturePreviewIds.Clear();
    }

    private void ClearRetainedPreviewCaches()
    {
        _decodedTexturePreviews.Clear();
        _decodedTextureOrder.Clear();
        _modelResultCache.Clear();
        _modelResultOrder.Clear();
        // ConditionalWeakTable has no Clear API. Replacing it removes this page's
        // strong cache root so old WPF MeshGeometry3D instances can be collected.
        _geometryCache = new ConditionalWeakTable<ModelPreviewMesh, CachedMeshGeometry>();
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

    private bool TryGetDecodedTexture(TextureInspectionItem texture, out LoadedTexturePreview preview)
        => _decodedTexturePreviews.TryGetValue(CreateTextureCacheKey(texture), out preview!);

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
            ? mesh.MaterialTextures.EnumerateRenderableInputs()
                .Concat(mesh.TextureIds)
                .Distinct()
                .ToArray()
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : []);

        var baseColor = useAutomaticMaterials
            ? mesh.MaterialTextures.Get(ModelPreviewTextureRole.BaseColor)
                .Concat(mesh.ColorTextureId is ulong color ? [color] : [])
                .Concat(textureIds)
                .FirstOrDefault(texturePreviews.ContainsKey)
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
            ? mesh.MaterialTextures.EnumerateRenderableInputs()
                .Concat(mesh.TextureIds)
                .Where(texturePreviews.ContainsKey)
                .Distinct()
            : (GetSelectedTextureIdForMesh(mesh, selectedTextureId) is ulong selected
                ? [selected]
                : Enumerable.Empty<ulong>());
        return string.Join(",", ids.OrderBy(static id => id));
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
        UpdateLocalizedPreviewLabels();
    }

    protected override void OnDispose()
    {
        _loadCancellation?.Cancel();
        ModelGroup = null;
        SelectedTexturePreview = null;
        ClearActiveTexturePreviews();
        ClearRetainedPreviewCaches();
        Meshes.Clear();
        Textures.Clear();
        Armors.Clear();
        _rebuildGate.Dispose();
        _localizationService.PropertyChanged -= LocalizationServiceOnPropertyChanged;
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
        int DxgiFormat);
    private sealed record CachedMeshGeometry(
        MeshGeometry3D Geometry,
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ);
    private sealed record ModelPreviewBuildResult(Model3DGroup Group, double Radius);
}
