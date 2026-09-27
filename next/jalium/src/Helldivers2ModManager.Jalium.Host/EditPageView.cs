using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.AI;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class EditPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Card = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private readonly DashboardWorkspace _workspace;
    private readonly ModData _mod;
    private readonly AiTranslationService _translationService;
    private readonly CancellationTokenSource _translationCancellation = new();
    private readonly LocalizationService _localization;
    private readonly Dispatcher _dispatcher;
    private readonly Action _back;
    private readonly Action<Exception> _reportError;
    private readonly Action<ImageSource>? _previewImage;
    private readonly StackPanel _options = new();
    private readonly TextBlock _status = new();
    private readonly Dictionary<string, string> _translations = new(StringComparer.Ordinal);
    private Button? _translateButton;
    private bool _isTranslating;
    private bool _disposed;

    public EditPageView(DashboardWorkspace workspace, ModData mod, AiTranslationService translationService,
        LocalizationService localization,
        Action back, Action<Exception> reportError, Action<ImageSource>? previewImage = null)
    {
        _workspace = workspace;
        _mod = mod;
        _translationService = translationService;
        _localization = localization;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _back = back;
        _reportError = reportError;
        _previewImage = previewImage;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var backButton = new Button { Width = 36, Height = 36, Content = Icon("\uE72B", 16),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        backButton.Click += (_, _) => _back();
        header.Children.Add(backButton);
        var title = new TextBlock { Text = _mod.Manifest.Name, FontSize = 24,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Children.Add(header);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
        var previewHint = new TextBlock { Text = _localization["EditPage.PreviewHint"],
            Foreground = Secondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 16, 0) };
        toolbar.Children.Add(previewHint);
        var translate = new Button { Content = _localization["EditPage.TranslateOptions"],
            MinHeight = 34, Padding = new Thickness(12, 6, 12, 6) };
        _translateButton = translate;
        translate.Click += async (_, _) => await TranslateAsync();
        toolbar.Children.Add(translate);
        _status.Foreground = Secondary;
        _status.Margin = new Thickness(12, 0, 0, 0);
        toolbar.Children.Add(_status);
        var toolbarBorder = new Border { Child = toolbar, Background = Card, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(toolbarBorder, 1);
        Children.Add(toolbarBorder);

        var scroll = new ScrollViewer { Content = new Border { Child = _options, Background = Card,
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16) }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        Children.Add(scroll);

        var done = new Button { Content = _localization["EditPage.Done"], MinHeight = 38,
            Padding = new Thickness(24, 8, 24, 8), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0) };
        done.Click += async (_, _) => await SaveAsync();
        Grid.SetRow(done, 3);
        Children.Add(done);
        RenderOptions();
        _ = LoadCachedTranslationsAsync();
    }

    private void RenderOptions()
    {
        _options.Children.Clear();
        if (_mod.Manifest is not V1ModManifest { Options: { } options })
        {
            _options.Children.Add(new TextBlock { Text = _localization["EditPage.NoOptionsToTranslate"],
                Foreground = Secondary, Margin = new Thickness(4) });
            return;
        }
        for (var index = 0; index < options.Count; index++)
        {
            var optionIndex = index;
            var option = options[index];
            var body = new Grid { Margin = new Thickness(4) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var image = LoadImage(option.Image, 100);
            if (image is not null)
            {
                body.Children.Add(image);
                Grid.SetColumn(image, 0);
            }
            var copy = new StackPanel { Margin = new Thickness(image is null ? 0 : 12, 0, 12, 0) };
            copy.Children.Add(new TextBlock { Text = option.Name, FontSize = 16,
                FontWeight = FontWeights.SemiBold, Foreground = Foreground });
            copy.Children.Add(new TextBlock { Text = option.Description, FontSize = 13,
                Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            AddTranslation(copy, option.Name, 15);
            AddTranslation(copy, option.Description, 12);
            if (option.SubOptions is { Count: > 0 } subOptions)
            {
                var combo = new ComboBox { ItemsSource = subOptions.Select(sub => new SubOptionDisplay(
                        sub.Name, sub.Description,
                        _translations.GetValueOrDefault(sub.Name),
                        _translations.GetValueOrDefault(sub.Description))).ToArray(),
                    SelectedIndex = _mod.SelectedOptions.ElementAtOrDefault(optionIndex), MinWidth = 180,
                    Margin = new Thickness(0, 8, 0, 0) };
                combo.ItemTemplate = CreateSubOptionTemplate();
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedIndex < 0 || optionIndex >= _mod.SelectedOptions.Length)
                        return;
                    _mod.SelectedOptions[optionIndex] = combo.SelectedIndex;
                    SaveStateAsync();
                };
                copy.Children.Add(combo);
            }
            Grid.SetColumn(copy, 1);
            body.Children.Add(copy);
            var enabled = new CheckBox { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top,
                IsChecked = optionIndex >= _mod.EnabledOptions.Length || _mod.EnabledOptions[optionIndex] };
            enabled.Click += (_, _) =>
            {
                if (optionIndex < _mod.EnabledOptions.Length)
                    _mod.EnabledOptions[optionIndex] = enabled.IsChecked == true;
                SaveStateAsync();
            };
            Grid.SetColumn(enabled, 2);
            body.Children.Add(enabled);
                _options.Children.Add(new Border { Child = body, Background = Paint(0x39, 0x39, 0x39),
                BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) });
        }
    }

    private void AddTranslation(StackPanel target, string source, double fontSize)
    {
        if (_translations.TryGetValue(source, out var translation)
            && !string.IsNullOrWhiteSpace(translation))
            target.Children.Add(new TextBlock { Text = translation, FontSize = fontSize,
                Foreground = Paint(0x4C, 0xD7, 0xB0), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0) });
    }

    private async Task LoadCachedTranslationsAsync()
    {
        if (_mod.Manifest is not V1ModManifest { Options: { } options })
            return;
        try
        {
            var cached = await _translationService.GetCachedTranslationsAsync(
                GetOptionTexts(options), _translationCancellation.Token);
            if (cached.Count == 0)
                return;
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                foreach (var (source, translation) in cached)
                    _translations[source] = translation;
                RenderOptions();
            });
        }
        catch (OperationCanceledException) when (_translationCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _reportError(ex);
        }
    }

    private async Task TranslateAsync()
    {
        if (_isTranslating || _mod.Manifest is not V1ModManifest { Options: { } options })
            return;
        var texts = GetOptionTexts(options).ToArray();
        if (texts.Length == 0)
        {
            _status.Text = _localization["EditPage.NoOptionsToTranslate"];
            return;
        }

        _isTranslating = true;
        if (_translateButton is not null)
            _translateButton.IsEnabled = false;
        _status.Text = _localization["EditPage.Translating"];
        try
        {
            var progress = new Progress<AiTranslationProgress>(item =>
            {
                _ = _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed) return;
                    _translations[item.Source] = item.Translation;
                    RenderOptions();
                    _status.Text = _localization["EditPage.TranslatingProgress"]
                        .Replace("{completed}", item.Completed.ToString())
                        .Replace("{total}", item.Total.ToString());
                });
            });
            var result = await _translationService.TranslateAsync(texts, progress: progress,
                cancellationToken: _translationCancellation.Token);
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                foreach (var (source, translation) in result.Translations)
                    _translations[source] = translation;
                RenderOptions();
                _status.Text = result.ApiTextCount == 0
                    ? _localization["EditPage.TranslationCompletedFromCache"]
                        .Replace("{local}", result.LocalCacheHits.ToString())
                    : _localization["EditPage.TranslationCompletedWithUsage"]
                        .Replace("{local}", result.LocalCacheHits.ToString())
                        .Replace("{api}", result.ApiTextCount.ToString())
                        .Replace("{hit}", result.PromptCacheHitTokens.ToString())
                        .Replace("{miss}", result.PromptCacheMissTokens.ToString())
                        .Replace("{output}", result.CompletionTokens.ToString());
            });
        }
        catch (OperationCanceledException) when (_translationCancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _status.Text = _localization["EditPage.TranslationFailed"];
                _reportError(ex);
            });
        }
        finally
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;
                _isTranslating = false;
                if (_translateButton is not null)
                    _translateButton.IsEnabled = true;
            });
        }
    }

    private static IEnumerable<string> GetOptionTexts(IEnumerable<ModOption> options) => options.SelectMany(option =>
        new[] { option.Name, option.Description }
            .Concat(option.SubOptions?.SelectMany(sub => new[] { sub.Name, sub.Description }) ?? []));

    private static DataTemplate CreateSubOptionTemplate()
    {
        var template = new DataTemplate(typeof(SubOptionDisplay));
        template.SetVisualTree(() =>
        {
            var panel = new StackPanel { Margin = new Thickness(4, 3, 4, 3) };
            var name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
                Foreground = Foreground };
            name.SetBinding(TextBlock.TextProperty, nameof(SubOptionDisplay.Name));
            panel.Children.Add(name);
            var description = new TextBlock { FontSize = 12, Foreground = Secondary,
                TextWrapping = TextWrapping.Wrap };
            description.SetBinding(TextBlock.TextProperty, nameof(SubOptionDisplay.Description));
            panel.Children.Add(description);
            var translatedName = new TextBlock { FontSize = 13,
                Foreground = Paint(0x4C, 0xD7, 0xB0), TextWrapping = TextWrapping.Wrap };
            translatedName.SetBinding(TextBlock.TextProperty, nameof(SubOptionDisplay.TranslatedName));
            panel.Children.Add(translatedName);
            var translatedDescription = new TextBlock { FontSize = 11,
                Foreground = Paint(0x4C, 0xD7, 0xB0), TextWrapping = TextWrapping.Wrap };
            translatedDescription.SetBinding(TextBlock.TextProperty, nameof(SubOptionDisplay.TranslatedDescription));
            panel.Children.Add(translatedDescription);
            return panel;
        });
        return template;
    }

    private async Task SaveAsync()
    {
        try
        {
            await _workspace.SaveCurrentAsync();
            _back();
        }
        catch (Exception ex) { _reportError(ex); }
    }

    private async void SaveStateAsync()
    {
        try { await _workspace.SaveCurrentAsync(); }
        catch (Exception ex) { _reportError(ex); }
    }

    private Image? LoadImage(string? relative, double size)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return null;
        try
        {
            var root = Path.GetFullPath(_mod.Directory.FullName);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path))
                return null;
            var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform,
                Source = BitmapImage.FromFile(path) };
            image.MouseLeftButtonDown += (_, e) =>
            {
                if (image.Source is null || _previewImage is null) return;
                _previewImage(image.Source);
                e.Handled = true;
            };
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { return null; }
    }

    private static TextBlock Icon(string glyph, double size) => new() { Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = size, Foreground = Foreground };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _translationCancellation.Cancel();
    }

    private static Brush Paint(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private sealed record SubOptionDisplay(
        string Name,
        string Description,
        string? TranslatedName,
        string? TranslatedDescription);
}
