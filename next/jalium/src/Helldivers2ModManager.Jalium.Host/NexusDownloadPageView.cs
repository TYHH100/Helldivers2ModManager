using Helldivers2ModManager.Exceptions.Nexus;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.Services.Nexus;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using System.Diagnostics;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class NexusDownloadPageView : Grid, IDisposable
{
    private static readonly HttpClient Pictures = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private readonly NexusDownloadWorkflow _workflow;
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly Func<IReadOnlyList<string>, Task<ModImportResult?>> _import;
    private readonly Action _back;
    private readonly Action<Exception> _reportError;
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _fetchPicture;
    private readonly TextBox _url = new();
    private readonly PasswordBox _apiKey = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _urlLabel = new();
    private readonly TextBlock _urlDescription = new();
    private readonly TextBlock _apiKeyLabel = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _modHeading = new();
    private readonly TextBlock _emptyHint = new();
    private readonly TextBlock _name = new();
    private readonly TextBlock _author = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _filesHeading = new();
    private readonly Image _picture = new();
    private readonly StackPanel _modInfo = new();
    private readonly ListBox _files = new();
    private readonly Button _backButton = new();
    private readonly Button _fetchButton = new();
    private readonly Button _browserButton = new();
    private readonly Button _downloadButton = new();
    private CancellationTokenSource? _fetchCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private Mod? _selectedMod;
    private bool _busy;
    private bool _disposed;

    internal Task PendingFetch { get; private set; } = Task.CompletedTask;
    internal Task PendingDownload { get; private set; } = Task.CompletedTask;
    internal Task PendingPicture { get; private set; } = Task.CompletedTask;

    public NexusDownloadPageView(INexusModsService service, SettingsService settings,
        LocalizationService localization,
        Func<IReadOnlyList<string>, Task<ModImportResult?>> import,
        Action back, Action<Exception> reportError,
        Func<Uri, CancellationToken, Task<byte[]>>? fetchPicture = null)
    {
        _workflow = new NexusDownloadWorkflow(service);
        _settings = settings;
        _localization = localization;
        _import = import;
        _back = back;
        _reportError = reportError;
        _fetchPicture = fetchPicture ?? Pictures.GetByteArrayAsync;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildHeader();
        BuildContent();
        _localization.PropertyChanged += OnLocalizationChanged;
        _apiKey.Password = settings.NexusApiKey ?? string.Empty;
        RefreshTexts();
        UpdateState();
    }

    private void BuildHeader()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _backButton.Content = new TextBlock { Text = "\u2190", FontSize = 18,
            FontFamily = new FontFamily("Segoe UI Symbol") };
        _backButton.Width = _backButton.Height = 36;
        _backButton.Margin = new Thickness(0, 0, 12, 0);
        _backButton.Click += (_, _) => _back();
        header.Children.Add(_backButton);
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        _title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        Children.Add(header);
    }

    private void BuildContent()
    {
        var sections = new StackPanel();
        var scroll = new ScrollViewer { Content = sections,
            Margin = new Thickness(0, 16, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) => sections.Width = Math.Max(0, scroll.ActualWidth - 20);
        Grid.SetRow(scroll, 1);
        Children.Add(scroll);

        var input = new StackPanel { Margin = new Thickness(16) };
        _urlLabel.FontSize = 15;
        _urlLabel.FontWeight = FontWeights.SemiBold;
        input.Children.Add(_urlLabel);
        _urlDescription.Foreground = Secondary;
        _urlDescription.Margin = new Thickness(0, 4, 0, 12);
        input.Children.Add(_urlDescription);
        var urlLine = new Grid();
        urlLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _url.Height = 36;
        _url.Margin = new Thickness(0, 0, 8, 0);
        _url.TextChanged += (_, _) => ClearMod();
        urlLine.Children.Add(_url);
        _fetchButton.Padding = new Thickness(16, 8, 16, 8);
        _fetchButton.Click += (_, _) => PendingFetch = FetchAsync();
        Grid.SetColumn(_fetchButton, 1);
        urlLine.Children.Add(_fetchButton);
        input.Children.Add(urlLine);
        _apiKeyLabel.Foreground = Secondary;
        _apiKeyLabel.Margin = new Thickness(0, 12, 0, 4);
        input.Children.Add(_apiKeyLabel);
        _apiKey.Height = 36;
        input.Children.Add(_apiKey);
        _status.Foreground = Secondary;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 8, 0, 0);
        input.Children.Add(_status);
        sections.Children.Add(Card(input));

        var details = new StackPanel { Margin = new Thickness(16) };
        _modHeading.FontSize = 15;
        _modHeading.FontWeight = FontWeights.SemiBold;
        details.Children.Add(_modHeading);
        _modInfo.Margin = new Thickness(0, 8, 0, 0);
        var identity = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _picture.Width = _picture.Height = 64;
        _picture.Stretch = Stretch.UniformToFill;
        identity.Children.Add(_picture);
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        _name.FontSize = 18;
        _name.FontWeight = FontWeights.SemiBold;
        _name.TextWrapping = TextWrapping.Wrap;
        text.Children.Add(_name);
        _author.Foreground = Secondary;
        _author.Margin = new Thickness(0, 4, 0, 0);
        text.Children.Add(_author);
        _summary.Foreground = Secondary;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 8, 0, 0);
        text.Children.Add(_summary);
        Grid.SetColumn(text, 1);
        identity.Children.Add(text);
        _modInfo.Children.Add(identity);
        _filesHeading.FontWeight = FontWeights.SemiBold;
        _filesHeading.Margin = new Thickness(0, 12, 0, 8);
        _modInfo.Children.Add(_filesHeading);
        _files.MaxHeight = 200;
        _files.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var fileContainer = new ControlTemplate(typeof(ListBoxItem));
        fileContainer.SetVisualTree(() => new Border
        {
            Name = "SelectionBorder", Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4), Child = new ContentPresenter(),
        });
        var selectedFile = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selectedFile.Setters.Add(new Setter(Border.BackgroundProperty,
            Paint(0x1D, 0x5E, 0x96), "SelectionBorder"));
        fileContainer.Triggers.Add(selectedFile);
        var fileStyle = new Style(typeof(ListBoxItem));
        fileStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        fileStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        fileStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        fileStyle.Setters.Add(new Setter(Control.TemplateProperty, fileContainer));
        _files.ItemContainerStyle = fileStyle;
        _files.ItemTemplate = CreateFileTemplate();
        _files.SelectionChanged += (_, _) => UpdateState();
        _modInfo.Children.Add(_files);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        _browserButton.Padding = new Thickness(16, 10, 16, 10);
        _browserButton.Click += (_, _) => OpenInBrowser();
        actions.Children.Add(_browserButton);
        _downloadButton.Padding = new Thickness(20, 10, 20, 10);
        _downloadButton.Margin = new Thickness(8, 0, 0, 0);
        _downloadButton.Click += (_, _) => PendingDownload = DownloadAsync();
        actions.Children.Add(_downloadButton);
        _modInfo.Children.Add(actions);
        details.Children.Add(_modInfo);
        _emptyHint.Foreground = Secondary;
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.Margin = new Thickness(0, 20, 0, 20);
        details.Children.Add(_emptyHint);
        sections.Children.Add(new Border { Child = details, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 16, 0, 0) });
    }

    private static DataTemplate CreateFileTemplate()
    {
        var template = new DataTemplate();
        template.SetVisualTree(() =>
        {
            var row = new Grid { Margin = new Thickness(4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var main = new StackPanel();
            var name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Foreground };
            name.SetBinding(TextBlock.TextProperty, "Name");
            main.Children.Add(name);
            var version = new TextBlock { FontSize = 12, Foreground = Secondary };
            version.SetBinding(TextBlock.TextProperty, "Version");
            main.Children.Add(version);
            row.Children.Add(main);
            var meta = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var size = new TextBlock { FontSize = 12, Foreground = Secondary };
            size.SetBinding(TextBlock.TextProperty, "Size");
            meta.Children.Add(size);
            var date = new TextBlock { FontSize = 11, Foreground = Secondary };
            date.SetBinding(TextBlock.TextProperty, "Date");
            meta.Children.Add(date);
            Grid.SetColumn(meta, 1);
            row.Children.Add(meta);
            return row;
        });
        return template;
    }

    internal async Task FetchAsync()
    {
        if (_busy || _disposed)
            return;
        var url = _url.Text.Trim();
        if (url.Length == 0)
        {
            _status.Text = _localization["NexusDownloadPage.EnterUrl"];
            return;
        }
        if (!NexusDownloadWorkflow.TryParseUrl(url, out _, out _))
        {
            _status.Text = _localization["NexusDownloadPage.ParseFailed"];
            return;
        }
        var key = _apiKey.Password.Trim();
        if (key.Length == 0)
        {
            _status.Text = _localization["NexusDownloadPage.NoApiKey"];
            return;
        }
        ClearMod();
        using var cancellation = new CancellationTokenSource();
        _fetchCancellation = cancellation;
        _busy = true;
        _status.Text = _localization["NexusDownloadPage.Fetching"];
        UpdateState();
        try
        {
            var (mod, files) = await _workflow.FetchAsync(url, key, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || url != _url.Text.Trim())
                return;
            _selectedMod = mod;
            _name.Text = mod.Name;
            _author.Text = mod.Author ?? string.Empty;
            _summary.Text = mod.Summary ?? string.Empty;
            var rows = files.Select(file => new NexusFileRow(file,
                file.Name ?? mod.Name, file.Version ?? string.Empty,
                file.SizeBytes is > 0 ? FormatSize(file.SizeBytes.Value) : string.Empty,
                file.UploadedAt?.ToString("yyyy-MM-dd") ?? string.Empty)).ToArray();
            _files.ItemsSource = rows;
            _files.SelectedItem = rows.FirstOrDefault(row => row.Model.IsPrimary == true)
                ?? rows.FirstOrDefault();
            _status.Text = rows.Length == 0 ? _localization["NexusDownloadPage.NoFiles"]
                : _localization["NexusDownloadPage.FoundPrefix"] + rows.Length
                    + _localization["MessageBox.NeedUpdateSuffix"];
            if (!string.Equals(_settings.NexusApiKey, key, StringComparison.Ordinal)
                && !_settings.IsReadonly)
            {
                _settings.NexusApiKey = key;
                await _settings.SaveAsync();
            }
            PendingPicture = LoadPictureAsync(mod.PictureUrl, mod, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                _status.Text = _localization["NexusDownloadPage.FetchFailedPrefix"] + ex.Message;
                _reportError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_fetchCancellation, cancellation))
                _fetchCancellation = null;
            _busy = false;
            UpdateState();
        }
    }

    internal async Task DownloadAsync()
    {
        if (_busy || _disposed || _selectedMod is null
            || _files.SelectedItem is not NexusFileRow selected)
        {
            if (!_busy && !_disposed)
                _status.Text = _localization["NexusDownloadPage.SelectFile"];
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        _busy = true;
        _status.Text = _localization["NexusDownloadPage.Downloading"];
        UpdateState();
        string? downloaded = null;
        try
        {
            downloaded = await _workflow.DownloadAsync(_url.Text, _selectedMod, selected.Model,
                _settings.TempDirectory, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested)
                return;
            _status.Text = _localization["NexusDownloadPage.Importing"];
            var result = await _import([downloaded]);
            if (_disposed)
                return;
            if (result is { Succeeded: > 0, Problems.Count: 0 })
            {
                _status.Text = _localization["NexusDownloadPage.ImportSuccess"];
                _back();
            }
            else
                _status.Text = result?.Problems.Any(problem => problem.IsError) == true
                    ? _localization["NexusDownloadPage.ImportProblems"]
                    : _localization["NexusDownloadPage.ImportWarnings"];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (NexusPremiumRequiredException)
        {
            _status.Text = _localization["NexusDownloadPage.PremiumRequired"];
            _reportError(new InvalidOperationException(_localization["NexusDownloadPage.PremiumMsg"]));
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _status.Text = _localization["NexusDownloadPage.DownloadFailedPrefix"] + ex.Message;
                _reportError(ex);
            }
        }
        finally
        {
            if (downloaded is not null && File.Exists(downloaded))
            {
                try { File.Delete(downloaded); }
                catch (IOException ex) { _reportError(ex); }
                catch (UnauthorizedAccessException ex) { _reportError(ex); }
            }
            if (ReferenceEquals(_downloadCancellation, cancellation))
                _downloadCancellation = null;
            _busy = false;
            UpdateState();
        }
    }

    private async Task LoadPictureAsync(string? url, Mod mod, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return;
        try
        {
            var bytes = await _fetchPicture(uri, cancellationToken);
            if (_disposed || cancellationToken.IsCancellationRequested
                || !ReferenceEquals(_selectedMod, mod) || bytes.Length > 4_194_304)
                return;
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            _picture.Source = image;
        }
        catch (Exception) { }
    }

    private void OpenInBrowser()
    {
        if (!NexusDownloadWorkflow.TryParseUrl(_url.Text, out _, out _))
        {
            _status.Text = _localization["NexusDownloadPage.EnterLinkFirst"];
            return;
        }
        try { Process.Start(new ProcessStartInfo(_url.Text) { UseShellExecute = true }); }
        catch (Exception ex) { _reportError(ex); }
    }

    private void ClearMod()
    {
        _selectedMod = null;
        _files.ItemsSource = null;
        _picture.Source = null;
        _name.Text = _author.Text = _summary.Text = string.Empty;
        UpdateState();
    }

    private void UpdateState()
    {
        _modInfo.Visibility = _selectedMod is null ? Visibility.Collapsed : Visibility.Visible;
        _emptyHint.Visibility = _selectedMod is null ? Visibility.Visible : Visibility.Collapsed;
        _fetchButton.IsEnabled = !_busy;
        _url.IsEnabled = !_busy;
        _apiKey.IsEnabled = !_busy;
        _browserButton.IsEnabled = !_busy && _selectedMod is not null;
        _downloadButton.IsEnabled = !_busy && _files.SelectedItem is NexusFileRow;
    }

    private void RefreshTexts()
    {
        _title.Text = _localization["NexusDownloadPage.Title"];
        _backButton.ToolTip = _localization["Common.Back"];
        _urlLabel.Text = _localization["NexusDownloadPage.UrlLabel"];
        _urlDescription.Text = _localization["NexusDownloadPage.UrlDesc"];
        _apiKeyLabel.Text = _localization["NexusDownloadPage.ApiKeyLabel"];
        _fetchButton.Content = _localization["NexusDownloadPage.FetchInfo"];
        _modHeading.Text = _localization["NexusDownloadPage.ModInfo"];
        _filesHeading.Text = _localization["NexusDownloadPage.DownloadFiles"];
        _browserButton.Content = _localization["NexusDownloadPage.OpenInBrowser"];
        _downloadButton.Content = _localization["NexusDownloadPage.DownloadImport"];
        _emptyHint.Text = _localization["NexusDownloadPage.EmptyHint"];
    }

    private void OnLocalizationChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs args) => RefreshTexts();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _fetchCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        _localization.PropertyChanged -= OnLocalizationChanged;
    }

    private static Border Card(UIElement child) => new()
    {
        Child = child, Background = Surface, BorderBrush = Stroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
    };

    private static string FormatSize(long size) => size >= 1_048_576
        ? $"{size / 1_048_576d:0.#} MB" : $"{size / 1024d:0.#} KB";

    private static Brush Paint(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private sealed record NexusFileRow(ModFile Model, string Name, string Version,
        string Size, string Date);
}
