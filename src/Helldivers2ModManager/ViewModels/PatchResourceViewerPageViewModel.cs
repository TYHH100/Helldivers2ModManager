using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

internal enum TexturePreviewChannel
{
    Rgb,
    Rgba,
    Alpha
}

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class PatchResourceViewerPageViewModel : PageViewModelBase
{
    private readonly ILogger<PatchResourceViewerPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navigationStore;
    private readonly ModService _modService;
    private readonly PatchResourceInspectionService _inspectionService;
    private readonly LocalizationService _localizationService;
    private TexturePreviewData? _loadedTexturePreview;
    private CancellationTokenSource? _textureCancellation;
    private readonly SemaphoreSlim _textureDecodeGate = new(1, 1);
    internal Task PendingTextureLoad { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    private bool _useOriginalTextureResolution;

    public override string Title => _localizationService["PatchResourceViewerPage.Title"];

    public ObservableCollection<ModData> Mods { get; } = [];
    public ObservableCollection<PatchTocInspectionItem> TocEntries { get; } = [];
    public ObservableCollection<GpuStreamInspectionItem> GpuStreams { get; } = [];
    public ObservableCollection<TextureInspectionItem> Textures { get; } = [];

    [ObservableProperty]
    private ModData? _selectedMod;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private TextureInspectionItem? _selectedTexture;

    [ObservableProperty]
    private ImageSource? _texturePreview;

    [ObservableProperty]
    private string _texturePreviewStatus = string.Empty;

    [ObservableProperty]
    private TexturePreviewChannel _textureChannel = TexturePreviewChannel.Rgb;

    public bool IsRgbChannel => TextureChannel == TexturePreviewChannel.Rgb;
    public bool IsRgbaChannel => TextureChannel == TexturePreviewChannel.Rgba;
    public bool IsAlphaChannel => TextureChannel == TexturePreviewChannel.Alpha;

    public PatchResourceViewerPageViewModel(
        ILogger<PatchResourceViewerPageViewModel> logger,
        IServiceProvider provider,
        ModService modService,
        PatchResourceInspectionService inspectionService,
        LocalizationService localizationService,
        AudioBankInspectionService audioInspectionService,
        AudioPlaybackService audioPlaybackService,
        ModTypeDetectionService modTypeDetectionService,
        TextBankInspectionService textInspectionService,
        Services.Parsing.LuaScriptInspectionService luaScriptInspectionService)
    {
        _logger = logger;
        _navigationStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _inspectionService = inspectionService;
        _localizationService = localizationService;
        _audioInspectionService = audioInspectionService;
        _audioPlaybackService = audioPlaybackService;
        _modTypeDetectionService = modTypeDetectionService;
        _textInspectionService = textInspectionService;
        _luaInspectionService = luaScriptInspectionService;
        _audioPositionTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _audioPositionTimer.Tick += AudioPositionTimerOnTick;
        _audioPlaybackService.PlaybackEnded += AudioPlaybackServiceOnPlaybackEnded;
        InitializeAudioView();
        InitializeTextView();
        _localizationService.PropertyChanged += LocalizationServiceOnPropertyChanged;

        _ = RefreshModsAsync();
    }

    [RelayCommand]
    private void GoBack() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    public void SetInitialMod(ModData mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (!Mods.Contains(mod))
            Mods.Add(mod);
        SelectedMod = mod;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshMods()
    {
        var previous = SelectedMod;
        await RefreshModsAsync();
        if (previous is not null && ReferenceEquals(previous, SelectedMod))
        {
            PendingResourceLoad = LoadSelectedModAsync(previous);
            await PendingResourceLoad;
        }
    }

    [RelayCommand]
    private void SetTextureChannel(string channel)
    {
        if (Enum.TryParse<TexturePreviewChannel>(channel, ignoreCase: true, out var parsed))
            TextureChannel = parsed;
    }

    partial void OnSelectedModChanged(ModData? value)
    {
        CancelResourceLoad();
        ClearResourceCollections();
        if (value is not null && !_isDisposed)
            PendingResourceLoad = LoadSelectedModAsync(value);
    }

    partial void OnSelectedTextureChanged(TextureInspectionItem? value)
    {
        CancelTexturePreview();
        if (!_isDisposed && value is not null && SelectedMod is not null)
            PendingTextureLoad = LoadTexturePreviewAsync(SelectedMod, value);
    }

    partial void OnUseOriginalTextureResolutionChanged(bool value)
    {
        OnSelectedTextureChanged(SelectedTexture);
    }

    private void CancelTexturePreview()
    {
        _textureCancellation?.Cancel();
        _textureCancellation?.Dispose();
        _textureCancellation = null;
        _loadedTexturePreview = null;
        TexturePreview = null;
        TexturePreviewStatus = string.Empty;
    }

    partial void OnTextureChannelChanged(TexturePreviewChannel value)
    {
        OnPropertyChanged(nameof(IsRgbChannel));
        OnPropertyChanged(nameof(IsRgbaChannel));
        OnPropertyChanged(nameof(IsAlphaChannel));
        ApplyTextureChannel();
    }

    private async Task RefreshModsAsync()
    {
        if (!_modService.Initialized)
        {
            StatusText = _localizationService["ModelPreviewPage.NotReady"];
            return;
        }

        var selectedGuid = SelectedMod?.Manifest.Guid;
        Mods.Clear();
        foreach (var mod in _modService.Mods.OrderBy(static mod => mod.Manifest.Name, StringComparer.CurrentCultureIgnoreCase))
            Mods.Add(mod);

        SelectedMod = Mods.FirstOrDefault(mod => mod.Manifest.Guid == selectedGuid) ?? Mods.FirstOrDefault();
        if (SelectedMod is null)
            StatusText = _localizationService["PatchResourceViewerPage.EmptyMods"];

        await Task.CompletedTask;
    }

    private async Task LoadTexturePreviewAsync(ModData mod, TextureInspectionItem texture)
    {
        var generation = _loadGeneration;
        _textureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _loadCancellation?.Token ?? _pageLifetimeCancellation.Token);
        var token = _textureCancellation.Token;
        var original = UseOriginalTextureResolution;
        _loadedTexturePreview = null;
        TexturePreview = null;
        TexturePreviewStatus = _localizationService["PatchResourceViewerPage.LoadingTexture"]
            .Replace("{id}", texture.TextureIdText);

        try
        {
            // Keep full-resolution decoding bounded; never silently label a reduced mip as original.
            const int originalPixelLimit = 67_108_864;
            if (original && (long)texture.Width * texture.Height > originalPixelLimit)
            {
                TexturePreviewStatus = _localizationService["PatchResourceViewerPage.OriginalTextureTooLarge"];
                return;
            }

            TexturePreviewData? preview;
            await _textureDecodeGate.WaitAsync(token);
            try
            {
                preview = await _inspectionService.PreviewTextureAsync(mod.Directory, texture,
                    maxPreviewPixels: original ? originalPixelLimit : 4_194_304, cancellationToken: token);
            }
            finally
            {
                _textureDecodeGate.Release();
            }
            if (token.IsCancellationRequested || !IsCurrentLoad(mod, generation) || !ReferenceEquals(texture, SelectedTexture))
                return;

            if (preview is null)
            {
                TexturePreviewStatus = _localizationService["PatchResourceViewerPage.TextureUnavailable"];
                return;
            }

            if (preview.EncodedImageBytes is null && preview.BgraPixels is null)
            {
                TexturePreviewStatus = _localizationService["PatchResourceViewerPage.TextureUnavailable"];
                return;
            }

            _loadedTexturePreview = preview;
            ApplyTextureChannel();
            TexturePreviewStatus = _localizationService["PatchResourceViewerPage.TextureLoaded"]
                .Replace("{width}", preview.Width.ToString())
                .Replace("{height}", preview.Height.ToString())
                .Replace("{format}", preview.Description);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode texture {TextureId}", texture.TextureIdText);
            if (!token.IsCancellationRequested && IsCurrentLoad(mod, generation) && ReferenceEquals(texture, SelectedTexture))
                TexturePreviewStatus = _localizationService["PatchResourceViewerPage.TextureUnavailable"];
        }
    }

    private void ApplyTextureChannel()
    {
        if (_loadedTexturePreview is null)
            return;

        var preview = _loadedTexturePreview;
        byte[] sourcePixels;
        int width;
        int height;
        if (preview.BgraPixels is not null)
        {
            sourcePixels = preview.BgraPixels;
            width = preview.Width;
            height = preview.Height;
        }
        else if (preview.EncodedImageBytes is not null)
        {
            using var encodedImage = new MemoryStream(preview.EncodedImageBytes, writable: false);
            var png = new BitmapImage();
            png.BeginInit();
            png.CacheOption = BitmapCacheOption.OnLoad;
            if (!UseOriginalTextureResolution)
                png.DecodePixelWidth = Math.Min(preview.Width, 2048);
            png.StreamSource = encodedImage;
            png.EndInit();

            var converted = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
            width = converted.PixelWidth;
            height = converted.PixelHeight;
            sourcePixels = new byte[checked(width * height * 4)];
            converted.CopyPixels(sourcePixels, width * 4, 0);
        }
        else
        {
            return;
        }

        var displayPixels = (byte[])sourcePixels.Clone();
        for (var offset = 0; offset < displayPixels.Length; offset += 4)
        {
            if (TextureChannel == TexturePreviewChannel.Alpha)
            {
                var alpha = displayPixels[offset + 3];
                displayPixels[offset] = alpha;
                displayPixels[offset + 1] = alpha;
                displayPixels[offset + 2] = alpha;
                displayPixels[offset + 3] = byte.MaxValue;
            }
            else if (TextureChannel == TexturePreviewChannel.Rgb)
            {
                displayPixels[offset + 3] = byte.MaxValue;
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, displayPixels, width * 4);
        bitmap.Freeze();
        TexturePreview = bitmap;
    }

    private void LocalizationServiceOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Title));
    }

    protected override void OnDispose()
    {
        _isDisposed = true;
        CancelTexturePreview();
        CancelResourceLoad();
        _audioPositionTimer.Tick -= AudioPositionTimerOnTick;
        _audioPlaybackService.PlaybackEnded -= AudioPlaybackServiceOnPlaybackEnded;
        ClearResourceCollections();
        ClearAudioInventoryCache();
        ClearTextInventoryCache();
        _luaInventoryCache.Clear();
        _luaInventoryOrder.Clear();
        _pageLifetimeCancellation.Cancel();
        _pageLifetimeCancellation.Dispose();
        _localizationService.PropertyChanged -= LocalizationServiceOnPropertyChanged;
    }
}
