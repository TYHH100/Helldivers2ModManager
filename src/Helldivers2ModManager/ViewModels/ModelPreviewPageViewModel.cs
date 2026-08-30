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
