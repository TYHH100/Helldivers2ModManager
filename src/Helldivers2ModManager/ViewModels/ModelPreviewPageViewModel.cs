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
    // 播放目标 ≥60fps：帧网格 1/60s、时间轴定时器 15ms。缓存按字节预算在插入时有界淘汰，
    // 不再按整段 clip 压缩离散帧数——那会把长动画压到每秒几个姿势的幻灯片。
    private const int AnimationFramesPerSecond = 60;
    private const int MaxCachedAnimationFrames = 240;
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
    private long _animationFrameCacheBytes;
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
    // 动画列表整体替换（见 SetAnimations）：万级条目下逐条 Add/Clear 的通知量随条目数
    // 平方增长，不可接受；且该列表已不再走 WPF 绑定，由 VirtualizedTextPicker 以
    // 代码驱动方式消费（见 Views/ModelPreviewPageView.xaml 的 WireAnimationPicker）。
    public IReadOnlyList<ModelPreviewAnimationChoice> Animations { get; private set; } = [];
    public IReadOnlyList<string> AnimationNames { get; private set; } = [];
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
    private bool _forceDecodeOversizedStreams;

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
    // 只读已解码 clip 的时长（未就绪时为 0），绝不在此触发解码——见
    // ModelPreviewAnimationOption.CachedLengthSeconds。UI 线程一旦解码就会冻结界面。
    public double SelectedAnimationDuration => SelectedAnimation?.Option.CachedLengthSeconds ?? 0;
    public int SelectedAnimationIndex
    {
        get
        {
            if (SelectedAnimation is not { } selected)
                return -1;
            var animations = Animations;
            for (var index = 0; index < animations.Count; index++)
            {
                if (ReferenceEquals(animations[index], selected))
                    return index;
            }
            return -1;
        }
    }
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
        LocalizationService localizationService,
        AudioBankInspectionService audioInspectionService,
        AudioPlaybackService audioPlaybackService,
        ModTypeDetectionService modTypeDetectionService,
        TextBankInspectionService textInspectionService)
    {
        _logger = logger;
        _navigationStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _inspectionService = inspectionService;
        _previewBackend = previewBackend;
        _gpuSkinningService = gpuSkinningService;
        _localizationService = localizationService;
        _audioInspectionService = audioInspectionService;
        _audioPlaybackService = audioPlaybackService;
        _modTypeDetectionService = modTypeDetectionService;
        _textInspectionService = textInspectionService;
        _localizationService.PropertyChanged += LocalizationServiceOnPropertyChanged;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _animationTimer.Tick += AnimationTimerOnTick;
        _audioPositionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _audioPositionTimer.Tick += AudioPositionTimerOnTick;
        _audioPlaybackService.PlaybackEnded += AudioPlaybackServiceOnPlaybackEnded;
        InitializeAudioView();
        InitializeTextView();

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

    /// <summary>
    /// 整体替换动画列表并同步构建显示名数组（一次属性通知，不产生 CollectionChanged
    /// 风暴）。名称数组与选择列表按下标一一对应，供代码驱动的下拉控件使用。
    /// </summary>
    internal void SetAnimations(IReadOnlyList<ModelPreviewAnimationChoice> animations)
    {
        ArgumentNullException.ThrowIfNull(animations);
        Animations = animations;
        AnimationNames = animations.Count == 0
            ? []
            : animations.Select(static choice => choice.DisplayName).ToArray();
        OnPropertyChanged(nameof(Animations));
        OnPropertyChanged(nameof(AnimationNames));
        OnPropertyChanged(nameof(HasAnimations));
    }

    /// <summary>下拉控件回传的源索引（-1 表示清空选择）。</summary>
    internal void SelectAnimationAt(int index)
    {
        var animations = Animations;
        SelectedAnimation = index >= 0 && index < animations.Count ? animations[index] : null;
    }

    internal string AnimationPickerPlaceholder => _localizationService["ModelPreviewPage.AnimationPlaceholder"];
    internal string AnimationSearchHint => _localizationService["ModelPreviewPage.AnimationSearchHint"];
    internal string AnimationNoMatchText => _localizationService["ModelPreviewPage.AnimationNoMatch"];

    internal sealed record ModelPreviewAnimationChoice(
        ModelPreviewAnimationLibrary Library,
        ModelPreviewAnimationOption Option,
        string SourceMarker = "")
    {
        public string DisplayName => Option.DisplayName + SourceMarker;
    }

    internal sealed record LoadedTexturePreview(
        ImageSource Image,
        TexturePreviewRole Role,
        long SourcePixelCount,
        // AlbedoIridescence 的 Alpha 强度（0..1）：>0 时预览给材质叠加流光高光层。
        double IridescenceStrength = 0);
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
