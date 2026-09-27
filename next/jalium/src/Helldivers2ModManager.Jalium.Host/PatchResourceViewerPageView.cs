using System.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Parsing;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class PatchResourceViewerPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private readonly LocalizationService _localization;
    private readonly Func<IReadOnlyList<ModData>> _mods;
    private readonly Func<ModData, CancellationToken, Task<PatchResourceInspectionResult>> _inspect;
    private readonly Func<ModData, TextureInspectionItem, int, CancellationToken, Task<TexturePreviewData?>> _preview;
    private readonly Func<ModData, CancellationToken, Task<AudioInventoryResult?>> _inspectAudio;
    private readonly Func<ModData, CancellationToken, Task<TextInventoryResult>> _inspectText;
    private readonly Func<ModData, CancellationToken, Task<LuaScriptInventoryResult>> _inspectLua;
    private readonly Func<ModData, string, CancellationToken, Task<LuaScriptInspectionService.LuaExtractionResult>> _extractLua;
    private readonly Action _back;
    private readonly ListBox _modList = new();
    private readonly DataGrid _toc = new();
    private readonly DataGrid _gpu = new();
    private readonly ListBox _textures = new();
    private readonly ListBox _luaEntries = new();
    private readonly TabControl _previewTabs = new();
    private readonly Image _textureImage = new();
    private readonly Image _zoomImage = new();
    private readonly Grid _zoomOverlay = new();
    private readonly Border _zoomViewport = new();
    private readonly Button _resetZoomButton = new();
    private readonly Button _closeZoomButton = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _previewStatus = new();
    private readonly TextBlock _modsHeading = new();
    private readonly TextBlock _originalLabel = new();
    private readonly TextBlock _refreshLabel = new();
    private readonly Button _backButton = new();
    private readonly Button _refreshButton = new();
    private readonly CheckBox _originalResolution = new();
    private readonly Button[] _channels = [new(), new(), new()];
    private readonly TabItem[] _tabs = [new(), new(), new(), new(), new(), new()];
    private readonly Dictionary<DataGridTextColumn, string> _columnKeys = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _textureCancellation;
    private CancellationTokenSource? _luaExtractionCancellation;
    private ModData? _selectedMod;
    private TextureInspectionItem? _selectedTexture;
    private TexturePreviewData? _loadedTexture;
    private byte[]? _rawPixels;
    private int _pixelWidth;
    private int _pixelHeight;
    private PatchTextureChannel _channel;
    private int _generation;
    private bool _changingMod;
    private bool _disposed;
    private LuaScriptInventoryResult _luaInventory = LuaScriptInventoryResult.Empty;

    internal Task PendingResourceLoad { get; private set; } = Task.CompletedTask;
    internal Task PendingTextureLoad { get; private set; } = Task.CompletedTask;

    public PatchResourceViewerPageView(LocalizationService localization,
        Func<IReadOnlyList<ModData>> mods,
        Func<ModData, CancellationToken, Task<PatchResourceInspectionResult>> inspect,
        Func<ModData, TextureInspectionItem, int, CancellationToken, Task<TexturePreviewData?>> preview,
        Func<ModData, CancellationToken, Task<AudioInventoryResult?>> inspectAudio,
        Func<ModData, CancellationToken, Task<TextInventoryResult>> inspectText,
        Func<ModData, CancellationToken, Task<LuaScriptInventoryResult>> inspectLua,
        Func<ModData, string, CancellationToken, Task<LuaScriptInspectionService.LuaExtractionResult>> extractLua,
        Action back, ModData? initialMod = null)
    {
        _localization = localization;
        _mods = mods;
        _inspect = inspect;
        _preview = preview;
        _inspectAudio = inspectAudio;
        _inspectText = inspectText;
        _inspectLua = inspectLua;
        _extractLua = extractLua;
        _back = back;
        InitializeAudio();
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildHeader();
        BuildWorkspace();
        BuildZoomOverlay();
        _modList.SelectionChanged += (_, _) =>
        {
            if (!_changingMod)
                SelectMod(_modList.SelectedItem as ModData);
        };
        _textures.SelectionChanged += (_, _) =>
        {
            _selectedTexture = _textures.SelectedItem as TextureInspectionItem;
            PendingTextureLoad = LoadTextureAsync();
        };
        _originalResolution.Click += (_, _) => PendingTextureLoad = LoadTextureAsync();
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RefreshMods(initialMod);
    }

    private void BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _backButton.Content = Icon("\uE72B");
        _backButton.Width = _backButton.Height = 36;
        _backButton.Margin = new Thickness(0, 0, 10, 0);
        _backButton.Click += (_, _) => _back();
        header.Children.Add(_backButton);
        var heading = new StackPanel();
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        heading.Children.Add(_title);
        _description.FontSize = 12;
        _description.Foreground = Secondary;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.Margin = new Thickness(0, 3, 0, 0);
        heading.Children.Add(_description);
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);
        var refresh = new StackPanel { Orientation = Orientation.Horizontal };
        refresh.Children.Add(Icon("\uE72C"));
        _refreshLabel.Margin = new Thickness(7, 0, 0, 0);
        refresh.Children.Add(_refreshLabel);
        _refreshButton.Content = refresh;
        _refreshButton.Padding = new Thickness(12, 7, 12, 7);
        _refreshButton.Click += (_, _) => RefreshMods(_selectedMod);
        Grid.SetColumn(_refreshButton, 2);
        header.Children.Add(_refreshButton);
        Children.Add(header);
    }

    private void BuildWorkspace()
    {
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _modsHeading.FontWeight = FontWeights.SemiBold;
        _modsHeading.Margin = new Thickness(14, 12, 14, 8);
        _modsHeading.Foreground = Foreground;
        left.Children.Add(_modsHeading);
        var modTemplate = new DataTemplate();
        modTemplate.SetVisualTree(() =>
        {
            var stack = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            var name = new TextBlock { FontWeight = FontWeights.SemiBold,
                Foreground = Foreground, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetBinding(TextBlock.TextProperty, "Manifest.Name");
            stack.Children.Add(name);
            var directory = new TextBlock { FontSize = 11, Foreground = Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            directory.SetBinding(TextBlock.TextProperty, "Directory.Name");
            stack.Children.Add(directory);
            return stack;
        });
        _modList.ItemTemplate = modTemplate;
        _modList.BorderThickness = new Thickness(0);
        _modList.Background = Brushes.Transparent;
        Grid.SetRow(_modList, 1);
        left.Children.Add(_modList);
        workspace.Children.Add(Card(left, 0));

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _status.FontSize = 12;
        _status.Foreground = Secondary;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 8);
        right.Children.Add(_status);
        _previewTabs.Background = Surface;
        _previewTabs.Foreground = Foreground;
        _previewTabs.BorderBrush = Stroke;
        _previewTabs.BorderThickness = new Thickness(1);
        _previewTabs.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _previewTabs.SizeChanged += (_, _) =>
        {
            var width = Math.Max(0, _previewTabs.ActualWidth - 24);
            foreach (var tab in _tabs.Skip(2))
                if (tab.Content is Grid content)
                    content.Width = width;
        };
        ConfigureGrid(_toc, ("#", "EntryIndex"), ("PatchResourceViewerPage.ColumnPatch", "PatchFile"),
            ("PatchResourceViewerPage.ColumnFileId", "FileIdText"),
            ("PatchResourceViewerPage.TypeId", "TypeIdText"),
            ("PatchResourceViewerPage.Type", "TypeNameText"),
            ("PatchResourceViewerPage.ColumnMain", "MainRangeText"),
            ("PatchResourceViewerPage.ColumnGpu", "GpuRangeText"),
            ("PatchResourceViewerPage.ColumnStream", "StreamRangeText"));
        ConfigureGrid(_gpu, ("PatchResourceViewerPage.ColumnPatch", "PatchFile"),
            ("PatchResourceViewerPage.ColumnUnit", "UnitIdText"),
            ("PatchResourceViewerPage.ColumnStream", "StreamIndex"),
            ("PatchResourceViewerPage.ColumnVertices", "VertexCount"),
            ("PatchResourceViewerPage.ColumnStride", "VertexStride"),
            ("PatchResourceViewerPage.ColumnIndices", "IndexCount"),
            ("PatchResourceViewerPage.ColumnComponents", "Components"),
            ("PatchResourceViewerPage.ColumnVertexSample", "VertexSample"));
        _tabs[0].Content = _toc;
        _tabs[1].Content = _gpu;
        _tabs[2].Content = BuildTextureTab();
        _tabs[3].Content = BuildAudioTab();
        _tabs[3].Visibility = Visibility.Collapsed;
        _tabs[4].Content = BuildTextTab();
        _tabs[4].Visibility = Visibility.Collapsed;
        _tabs[5].Content = BuildLuaTab();
        _tabs[5].Visibility = Visibility.Collapsed;
        foreach (var tab in _tabs)
            _previewTabs.Items.Add(tab);
        Grid.SetRow(_previewTabs, 1);
        right.Children.Add(_previewTabs);
        Grid.SetColumn(right, 2);
        workspace.Children.Add(right);
        Grid.SetRow(workspace, 1);
        Children.Add(workspace);
    }

    private Grid BuildTextureTab()
    {
        var root = new Grid { Margin = new Thickness(4, 8, 4, 4) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var template = new DataTemplate();
        template.SetVisualTree(() =>
        {
            var stack = new StackPanel { Margin = new Thickness(7, 5, 7, 5) };
            foreach (var (property, size) in new[]
                     { ("TextureIdText", 11), ("SizeText", 11), ("FormatText", 11) })
            {
                var label = new TextBlock { FontSize = size, Foreground = Secondary };
                label.SetBinding(TextBlock.TextProperty, property);
                stack.Children.Add(label);
            }
            return stack;
        });
        _textures.ItemTemplate = template;
        root.Children.Add(_textures);
        var preview = new Grid { Margin = new Thickness(10) };
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _previewStatus.FontSize = 12;
        _previewStatus.Foreground = Secondary;
        _previewStatus.TextWrapping = TextWrapping.Wrap;
        controls.Children.Add(_previewStatus);
        var channelControls = new StackPanel { Orientation = Orientation.Horizontal };
        _originalResolution.Content = _originalLabel;
        _originalResolution.Margin = new Thickness(0, 0, 12, 0);
        channelControls.Children.Add(_originalResolution);
        for (var index = 0; index < _channels.Length; index++)
        {
            var channel = (PatchTextureChannel)index;
            _channels[index].Content = channel == PatchTextureChannel.Alpha ? "A" : channel.ToString().ToUpperInvariant();
            _channels[index].Width = channel == PatchTextureChannel.Alpha ? 32 : 52;
            _channels[index].Height = 28;
            _channels[index].Margin = new Thickness(index == 0 ? 0 : 4, 0, 0, 0);
            _channels[index].Click += (_, _) => SelectChannel(channel);
            channelControls.Children.Add(_channels[index]);
        }
        Grid.SetColumn(channelControls, 1);
        controls.Children.Add(channelControls);
        preview.Children.Add(controls);
        _textureImage.Stretch = Stretch.Uniform;
        _textureImage.MouseLeftButtonUp += (_, args) =>
        {
            if (_textureImage.Source is null)
                return;
            OpenTextureZoom();
            args.Handled = true;
        };
        Grid.SetRow(_textureImage, 1);
        preview.Children.Add(_textureImage);
        var previewCard = Card(preview, 10);
        Grid.SetColumn(previewCard, 2);
        root.Children.Add(previewCard);
        SelectChannel(PatchTextureChannel.Rgb);
        return root;
    }

    private void ConfigureGrid(DataGrid grid, params (string Header, string Property)[] columns)
    {
        grid.AutoGenerateColumns = false;
        grid.IsReadOnly = true;
        grid.CanUserAddRows = false;
        grid.FontSize = 11;
        foreach (var (header, property) in columns)
        {
            var column = new DataGridTextColumn { Header = header == "#" ? header : _localization[header],
                Binding = new Binding(property) };
            grid.Columns.Add(column);
            if (header != "#")
                _columnKeys[column] = header;
        }
    }

    private void RefreshMods(ModData? preferred)
    {
        if (_disposed)
            return;
        _changingMod = true;
        try
        {
            var mods = _mods().OrderBy(mod => mod.Manifest.Name,
                StringComparer.CurrentCultureIgnoreCase).ToArray();
            _modList.ItemsSource = mods;
            _modList.SelectedItem = mods.FirstOrDefault(mod => mod.Manifest.Guid == preferred?.Manifest.Guid)
                ?? mods.FirstOrDefault();
        }
        finally { _changingMod = false; }
        SelectMod(_modList.SelectedItem as ModData, force: true);
    }

    private void SelectMod(ModData? mod, bool force = false)
    {
        if (!force && ReferenceEquals(mod, _selectedMod))
            return;
        CloseTextureZoom();
        _selectedMod = mod;
        PendingResourceLoad = LoadResourcesAsync();
    }

    private async Task LoadResourcesAsync()
    {
        _loadCancellation?.Cancel();
        _textureCancellation?.Cancel();
        _luaExtractionCancellation?.Cancel();
        var mod = _selectedMod;
        var generation = ++_generation;
        _toc.ItemsSource = null;
        _gpu.ItemsSource = null;
        _textures.ItemsSource = null;
        ClearAudio();
        ClearText();
        ClearLua();
        _textureImage.Source = null;
        _refreshButton.IsEnabled = mod is null;
        if (mod is null)
        {
            _status.Text = _localization["PatchResourceViewerPage.EmptyMods"];
            _refreshButton.IsEnabled = true;
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _status.Text = _localization["PatchResourceViewerPage.Loading"]
            .Replace("{name}", mod.Manifest.Name);
        try
        {
            var result = await _inspect(mod, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation)
                return;
            _toc.ItemsSource = result.TocEntries;
            _gpu.ItemsSource = result.GpuStreams;
            _textures.ItemsSource = result.Textures;
            _status.Text = _localization["PatchResourceViewerPage.Loaded"]
                .Replace("{patches}", result.PatchFileCount.ToString())
                .Replace("{toc}", result.TocEntries.Count.ToString())
                .Replace("{streams}", result.GpuStreams.Count.ToString());
            if (!string.IsNullOrWhiteSpace(result.Error))
                _status.Text += " " + result.Error;
            _textures.SelectedItem = result.Textures.FirstOrDefault();
            var audio = await _inspectAudio(mod, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation)
                return;
            if (audio is null)
                _status.Text += " " + _localization["ModelPreviewPage.AudioMultiOptionSkipped"];
            else
                ApplyAudio(audio, result);
            var text = await _inspectText(mod, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation)
                return;
            ApplyText(text, result);
            var lua = await _inspectLua(mod, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _generation)
                return;
            ApplyLua(lua, result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed && generation == _generation)
                _status.Text = _localization["PatchResourceViewerPage.LoadFailed"]
                    .Replace("{message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            if (!_disposed && generation == _generation)
                _refreshButton.IsEnabled = true;
        }
    }

    private void RefreshTexts()
    {
        _title.Text = _localization["PatchResourceViewerPage.Title"];
        _description.Text = _localization["PatchResourceViewerPage.Description"];
        _modsHeading.Text = _localization["ModelPreviewPage.Mods"];
        _refreshLabel.Text = _localization["ModelPreviewPage.Refresh"];
        _backButton.ToolTip = _localization["Common.Back"];
        _tabs[0].Header = _localization["PatchResourceViewerPage.Toc"];
        _tabs[1].Header = _localization["PatchResourceViewerPage.GpuStreams"];
        _tabs[2].Header = _localization["PatchResourceViewerPage.Textures"];
        _tabs[3].Header = _localization["ModelPreviewPage.AudioTab"];
        _tabs[4].Header = _localization["ModelPreviewPage.TextTab"];
        _tabs[5].Header = _localization["ModelPreviewPage.LuaTab"];
        RefreshAudioTexts();
        RefreshAudioRowsLocalization();
        RefreshTextTexts();
        RefreshTextRowsLocalization();
        RefreshLuaTexts();
        foreach (var (column, key) in _columnKeys)
            column.Header = _localization[key];
        _originalLabel.Text = _localization["ModelPreviewPage.OriginalTextureResolution"];
        _originalResolution.ToolTip = _localization["PatchResourceViewerPage.OriginalTextureResolutionTip"];
        _resetZoomButton.ToolTip = _localization["PatchResourceViewerPage.ResetZoom"];
        _closeZoomButton.ToolTip = _localization["PatchResourceViewerPage.ClosePreview"];
        if (_selectedTexture is null)
            _previewStatus.Text = _localization["PatchResourceViewerPage.SelectTexture"];
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs args) => RefreshTexts();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseTextureZoom();
        _generation++;
        _loadCancellation?.Cancel();
        _textureCancellation?.Cancel();
        _luaExtractionCancellation?.Cancel();
        DisposeAudio();
        _localization.PropertyChanged -= OnLocalizationChanged;
    }

    private static Border Card(UIElement child, double padding) => new()
    {
        Child = child, Padding = new Thickness(padding), Background = Surface,
        BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
    };

    private static void ConfigureGroupedList(ListBox list)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var itemTemplate = new ControlTemplate(typeof(ListBoxItem));
        itemTemplate.SetVisualTree(() => new ContentPresenter());
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Stretch));
        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));
        list.ItemContainerStyle = itemStyle;
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(list, true);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        list.ItemsPanel = new ItemsPanelTemplate();
        list.ItemsPanel.SetVisualTree(() => new VirtualizingStackPanel
        {
            Orientation = Orientation.Vertical,
            VirtualizationMode = VirtualizationMode.Recycling,
            ScrollUnit = ScrollUnit.Pixel,
        });
    }

    private static Border CreateGroupHeader()
    {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Foreground };
            title.SetBinding(TextBlock.TextProperty, "Header");
            row.Children.Add(title);
            var patch = new TextBlock { FontSize = 11, Foreground = Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0) };
            patch.SetBinding(TextBlock.TextProperty, "PatchPath");
            Grid.SetColumn(patch, 1);
            row.Children.Add(patch);
            return new Border { Child = row, Background = Surface,
                BorderBrush = Stroke, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(0, 2, 0, 4) };
    }

    private sealed record GroupedListRow<T>(T? Entry, string Header, string PatchPath) where T : class
    {
        public Visibility HeaderVisibility => Entry is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EntryVisibility => Entry is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TextBlock Icon(string glyph) => new()
    {
        Text = glyph switch
        {
            "\uE72B" => "\u2190",
            "\uE72C" => "\u21BB",
            "\uE768" => "\u25B6",
            "\uE769" => "\u23F8",
            "\uE71A" => "\u25A0",
            "\uE711" => "\u00D7",
            _ => glyph,
        },
        FontFamily = new FontFamily("Segoe UI Symbol"),
        FontSize = 16, Foreground = Foreground,
    };

    private static Brush Paint(byte red, byte green, byte blue)
        => new SolidColorBrush(Color.FromRgb(red, green, blue));
}
