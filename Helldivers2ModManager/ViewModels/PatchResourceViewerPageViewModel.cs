using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using CorePreview = Helldivers2ModManager.Core.Preview;
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

internal sealed partial class PatchResourceViewerPageViewModel : PageViewModelBase
{
    private readonly ILogger<PatchResourceViewerPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navigationStore;
    private readonly ModService _modService;
    private readonly CorePreview.PatchResourceInspector _inspectionService;
    private readonly LocalizationService _localizationService;
    private CorePreview.TexturePreviewData? _loadedTexturePreview;

    public override string Title => _localizationService["PatchResourceViewerPage.Title"];

    public ObservableCollection<ModData> Mods { get; } = [];
    public ObservableCollection<CorePreview.PatchTocInspectionItem> TocEntries { get; } = [];
    public ObservableCollection<CorePreview.GpuStreamInspectionItem> GpuStreams { get; } = [];
    public ObservableCollection<CorePreview.TextureInspectionItem> Textures { get; } = [];

    [ObservableProperty]
    private ModData? _selectedMod;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private CorePreview.TextureInspectionItem? _selectedTexture;

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
        CorePreview.PatchResourceInspector inspectionService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navigationStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _modService = modService;
        _inspectionService = inspectionService;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += LocalizationServiceOnPropertyChanged;

        _ = RefreshModsAsync();
    }

    [RelayCommand]
    private void GoBack() => _navigationStore.Value.Navigate<DashboardPageViewModel>();

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshMods() => await RefreshModsAsync();

    [RelayCommand]
    private void SetTextureChannel(string channel)
    {
        if (Enum.TryParse<TexturePreviewChannel>(channel, ignoreCase: true, out var parsed))
            TextureChannel = parsed;
    }

    partial void OnSelectedModChanged(ModData? value)
    {
        if (value is not null)
            _ = LoadSelectedModAsync(value);
    }

    partial void OnSelectedTextureChanged(CorePreview.TextureInspectionItem? value)
    {
        if (value is not null && SelectedMod is not null)
            _ = LoadTexturePreviewAsync(SelectedMod, value);
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

    private async Task LoadSelectedModAsync(ModData mod)
    {
        IsLoading = true;
        StatusText = _localizationService["PatchResourceViewerPage.Loading"].Replace("{name}", mod.Manifest.Name);
        TocEntries.Clear();
        GpuStreams.Clear();
        Textures.Clear();
        SelectedTexture = null;
        _loadedTexturePreview = null;
        TexturePreview = null;
        TexturePreviewStatus = _localizationService["PatchResourceViewerPage.SelectTexture"];

        try
        {
            var result = await _inspectionService.InspectAsync(mod.Directory);
            if (!ReferenceEquals(mod, SelectedMod))
                return;

            foreach (var item in result.TocEntries)
                TocEntries.Add(item);
            foreach (var item in result.GpuStreams)
                GpuStreams.Add(item);
            foreach (var item in result.Textures)
                Textures.Add(item);

            SelectedTexture = Textures.FirstOrDefault();

            StatusText = string.IsNullOrWhiteSpace(result.Error)
                ? _localizationService["PatchResourceViewerPage.Loaded"]
                    .Replace("{patches}", result.PatchFileCount.ToString())
                    .Replace("{toc}", TocEntries.Count.ToString())
                    .Replace("{streams}", GpuStreams.Count.ToString())
                : _localizationService["PatchResourceViewerPage.LoadedWithWarning"].Replace("{message}", result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inspect patch resources for {Mod}", mod.Manifest.Name);
            StatusText = _localizationService["PatchResourceViewerPage.LoadFailed"].Replace("{message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(mod, SelectedMod))
                IsLoading = false;
        }
    }

    private async Task LoadTexturePreviewAsync(ModData mod, CorePreview.TextureInspectionItem texture)
    {
        _loadedTexturePreview = null;
        TexturePreview = null;
        TexturePreviewStatus = _localizationService["PatchResourceViewerPage.LoadingTexture"]
            .Replace("{id}", texture.TextureIdText);

        try
        {
            var preview = await _inspectionService.PreviewTextureAsync(mod.Directory, texture);
            if (!ReferenceEquals(mod, SelectedMod) || !ReferenceEquals(texture, SelectedTexture))
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode texture {TextureId}", texture.TextureIdText);
            if (ReferenceEquals(texture, SelectedTexture))
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
        _localizationService.PropertyChanged -= LocalizationServiceOnPropertyChanged;
    }
}
