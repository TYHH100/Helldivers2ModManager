using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Models;
using Helldivers2ModManager.Frontend.Services;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed class PatchResourceViewerPageViewModel : FrontendPageViewModel
{
    private readonly PatchResourceInspector _inspector;
    private readonly ModLibraryService _library;
    private readonly ModSelectionStore _selection;
    private readonly LocalizationCatalog _localization;
    private CancellationTokenSource? _textureCancellation;

    public ObservableCollection<ModItem> Mods { get; } = [];
    public ObservableCollection<PatchTocInspectionItem> TocEntries { get; } = [];
    public ObservableCollection<GpuStreamInspectionItem> GpuStreams { get; } = [];
    public ObservableCollection<TextureInspectionItem> Textures { get; } = [];

    private ModItem? _selectedMod;
    private bool _isBusy;
    private string _status = string.Empty;
    private TextureInspectionItem? _selectedTexture;
    private ImageSource? _texturePreview;
    private string _textureStatus = string.Empty;

    public ModItem? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (SetProperty(ref _selectedMod, value) && value is not null)
            {
                _ = LoadAsync(value);
            }
        }
    }

    public TextureInspectionItem? SelectedTexture
    {
        get => _selectedTexture;
        set
        {
            if (SetProperty(ref _selectedTexture, value) && _selectedMod is not null && value is not null)
            {
                _ = LoadTextureAsync(_selectedMod, value);
            }
        }
    }

    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ImageSource? TexturePreview { get => _texturePreview; private set => SetProperty(ref _texturePreview, value); }
    public string TextureStatus { get => _textureStatus; private set => SetProperty(ref _textureStatus, value); }
    public ICommand RefreshCommand { get; }

    public override string Title => _localization.GetString("Nav.ResourceViewer");

    public PatchResourceViewerPageViewModel(
        PatchResourceInspector inspector,
        ModLibraryService library,
        ModSelectionStore selection,
        LocalizationCatalog localization)
    {
        _inspector = inspector;
        _library = library;
        _selection = selection;
        _localization = localization;
        RefreshCommand = new DelegateCommand(async _ => await LoadModsAsync());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var preserve = SelectedMod;
        await LoadModsAsync().ConfigureAwait(true);
        SelectedMod = _selection.ResourceViewer ?? preserve ?? Mods.FirstOrDefault();
        _selection.ResourceViewer = null;
    }

    private async Task LoadModsAsync()
    {
        var result = await _library.LoadAsync().ConfigureAwait(true);
        Mods.Clear();
        foreach (var mod in result.Mods.OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Mods.Add(mod);
        }
    }

    private async Task LoadAsync(ModItem mod)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = string.Format(_localization.GetString("Resource.LoadingFormat"), mod.Name);
        TocEntries.Clear();
        GpuStreams.Clear();
        Textures.Clear();
        SelectedTexture = null;
        TexturePreview = null;
        TextureStatus = _localization.GetString("Resource.SelectTexture");
        try
        {
            var result = await _inspector.InspectAsync(mod.Directory).ConfigureAwait(true);
            if (!ReferenceEquals(mod, SelectedMod))
            {
                return;
            }

            foreach (var item in result.TocEntries) TocEntries.Add(item);
            foreach (var item in result.GpuStreams) GpuStreams.Add(item);
            foreach (var item in result.Textures) Textures.Add(item);
            SelectedTexture = Textures.FirstOrDefault();
            Status = string.IsNullOrWhiteSpace(result.Error)
                ? string.Format(_localization.GetString("Resource.LoadedFormat"), result.PatchFileCount, TocEntries.Count, GpuStreams.Count)
                : result.Error;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Status = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(mod, SelectedMod))
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadTextureAsync(ModItem mod, TextureInspectionItem texture)
    {
        _textureCancellation?.Cancel();
        _textureCancellation?.Dispose();
        _textureCancellation = new CancellationTokenSource();
        var token = _textureCancellation.Token;
        TexturePreview = null;
        TextureStatus = string.Format(_localization.GetString("Resource.TextureLoadingFormat"), texture.TextureIdText);
        try
        {
            var preview = await _inspector.PreviewTextureAsync(mod.Directory, texture, cancellationToken: token)
                .ConfigureAwait(true);
            if (!ReferenceEquals(texture, SelectedTexture))
            {
                return;
            }

            if (preview is null || (preview.BgraPixels is null && preview.EncodedImageBytes is null))
            {
                TextureStatus = _localization.GetString("Resource.TextureUnavailable");
                return;
            }

            TexturePreview = CreateImage(preview);
            TextureStatus = string.Format(
                _localization.GetString("Resource.TextureLoadedFormat"),
                preview.Width,
                preview.Height,
                preview.Description);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            TextureStatus = _localization.GetString("Resource.TextureUnavailable");
        }
    }

    private static BitmapSource CreateImage(TexturePreviewData preview)
    {
        if (preview.BgraPixels is not null)
        {
            var bitmap = BitmapSource.Create(
                preview.Width,
                preview.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                preview.BgraPixels,
                preview.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        using var stream = new MemoryStream(preview.EncodedImageBytes!, false);
        var png = new BitmapImage();
        png.BeginInit();
        png.CacheOption = BitmapCacheOption.OnLoad;
        png.DecodePixelWidth = Math.Min(preview.Width, 2048);
        png.StreamSource = stream;
        png.EndInit();
        png.Freeze();
        return png;
    }
}
