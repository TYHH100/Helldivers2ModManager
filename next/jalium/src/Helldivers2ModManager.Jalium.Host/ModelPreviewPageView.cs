using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Data;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class ModelPreviewPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private readonly LocalizationService _localization;
    private readonly Func<IReadOnlyList<ModData>> _mods;
    private readonly Func<ModData, bool[], int[], bool, CancellationToken, Task<ModelPreviewResult>> _preview;
    private readonly Func<ModData, TextureInspectionItem, int, CancellationToken, Task<TexturePreviewData?>> _texturePreview;
    private readonly Action _back;
    private readonly Action<ModData?> _openPatchResources;
    private readonly ListBox _modList = new();
    private readonly ModelPreviewScene _scene = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _modsHeading = new();
    private readonly TextBlock _partsHint = new();
    private readonly TextBlock _viewHeading = new();
    private readonly TextBlock _armorHeading = new();
    private readonly TextBlock _noOptions = new();
    private readonly TextBlock _hiddenSummary = new();
    private readonly TextBlock _textureHeading = new();
    private readonly TextBlock _resolutionHint = new();
    private readonly TextBlock _forceDecodeHint = new();
    private readonly Button _backButton = new();
    private readonly Button _resourcesButton = new();
    private readonly Button _refreshButton = new();
    private readonly Button[] _cameraButtons = [new(), new(), new(), new()];
    private readonly ComboBox _armor = new();
    private readonly ComboBox _textures = new();
    private readonly RadioButton _stocky = new();
    private readonly RadioButton _slim = new();
    private readonly CheckBox _automaticMaterials = new();
    private readonly CheckBox _originalResolution = new();
    private readonly CheckBox _showFiltered = new();
    private readonly CheckBox _isolateMesh = new();
    private readonly CheckBox _forceDecode = new();
    private readonly StackPanel _optionRows = new();
    private readonly DataGrid _meshGrid = new();
    private readonly Border _loadingOverlay = new();
    private readonly TabItem _optionTab = new();
    private readonly TabItem _meshTab = new();
    private readonly Dictionary<DataGridTextColumn, string> _columnKeys = [];
    private readonly List<ModelPreviewOptionRow> _options = [];
    private readonly ModData? _initialMod;
    private CancellationTokenSource? _loadCancellation;
    private ModData? _selectedMod;
    private ModelPreviewResult? _result;
    private ModelPreviewSelection _selection = new([], 0);
    private ModelPreviewMesh? _selectedMesh;
    private bool _changingMod;
    private bool _changingSelection;
    private bool _disposed;

    internal Task PendingLoad { get; private set; } = Task.CompletedTask;
    internal ModelPreviewScene Scene => _scene;

    public ModelPreviewPageView(LocalizationService localization,
        Func<IReadOnlyList<ModData>> mods,
        Func<ModData, bool[], int[], bool, CancellationToken, Task<ModelPreviewResult>> preview,
        Func<ModData, TextureInspectionItem, int, CancellationToken, Task<TexturePreviewData?>> texturePreview,
        Action back, Action<ModData?> openPatchResources, ModData? initialMod = null)
    {
        _localization = localization;
        _mods = mods;
        _preview = preview;
        _texturePreview = texturePreview;
        _back = back;
        _openPatchResources = openPatchResources;
        _initialMod = initialMod;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        BuildHeader();
        BuildWorkspace();
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RefreshMods(initialMod);
    }

    private void BuildHeader()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _backButton.Width = _backButton.Height = 36;
        _backButton.Content = new TextBlock { Text = "\u2190", FontSize = 18,
            FontFamily = new FontFamily("Segoe UI Symbol") };
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _resourcesButton.Padding = new Thickness(12, 7, 12, 7);
        _resourcesButton.Margin = new Thickness(0, 0, 8, 0);
        _resourcesButton.Click += (_, _) => _openPatchResources(_selectedMod);
        actions.Children.Add(_resourcesButton);
        _refreshButton.Padding = new Thickness(12, 7, 12, 7);
        _refreshButton.Click += (_, _) => RefreshMods(_selectedMod);
        actions.Children.Add(_refreshButton);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        Children.Add(header);
        _status.FontSize = 12;
        _status.Foreground = Secondary;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_status, 1);
        Children.Add(_status);
    }

    private void BuildWorkspace()
    {
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 180 });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var modPanel = new Grid();
        modPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        modPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _modsHeading.FontWeight = FontWeights.SemiBold;
        _modsHeading.Margin = new Thickness(14, 12, 14, 8);
        modPanel.Children.Add(_modsHeading);
        var modTemplate = new DataTemplate();
        modTemplate.SetVisualTree(() =>
        {
            var row = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            var name = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Foreground,
                TextWrapping = TextWrapping.Wrap, MaxHeight = 36 };
            name.SetBinding(TextBlock.TextProperty, "Manifest.Name");
            row.Children.Add(name);
            var path = new TextBlock { FontSize = 11, Foreground = Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            path.SetBinding(TextBlock.TextProperty, "Directory.Name");
            row.Children.Add(path);
            return row;
        });
        _modList.ItemTemplate = modTemplate;
        _modList.Background = Brushes.Transparent;
        _modList.BorderThickness = new Thickness(0);
        _modList.SelectionChanged += (_, _) =>
        {
            if (!_changingMod)
                SelectMod(_modList.SelectedItem as ModData);
        };
        Grid.SetRow(_modList, 1);
        modPanel.Children.Add(_modList);
        workspace.Children.Add(Card(modPanel, 0));

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230), MinHeight = 120 });
        BuildControls(right);
        BuildViewport(right);
        BuildTabs(right);
        Grid.SetColumn(right, 2);
        workspace.Children.Add(right);
        Grid.SetRow(workspace, 2);
        Children.Add(workspace);
    }

    private void BuildControls(Grid right)
    {
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _partsHint.Foreground = Secondary;
        _partsHint.TextWrapping = TextWrapping.Wrap;
        _partsHint.Margin = new Thickness(2, 0, 2, 0);
        controls.Children.Add(_partsHint);
        var groups = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        _viewHeading.Foreground = Secondary;
        _viewHeading.VerticalAlignment = VerticalAlignment.Center;
        _viewHeading.Margin = new Thickness(0, 0, 8, 0);
        views.Children.Add(_viewHeading);
        for (var index = 0; index < _cameraButtons.Length; index++)
        {
            var mode = index;
            var button = _cameraButtons[index];
            button.Height = 30;
            button.MinWidth = 52;
            button.Padding = new Thickness(8, 4, 8, 4);
            button.Margin = new Thickness(0, 0, index == 3 ? 0 : 6, 0);
            button.Click += (_, _) =>
            {
                switch (mode)
                {
                    case 0: _scene.FrontView(); break;
                    case 1: _scene.SideView(); break;
                    case 2: _scene.TopView(); break;
                    default: _scene.ResetCamera(); break;
                }
            };
            views.Children.Add(button);
        }
        groups.Children.Add(ToolGroup(views));
        BuildAnimationControls(groups);
        var shape = new StackPanel { Orientation = Orientation.Horizontal };
        shape.Children.Add(Label("ModelPreviewPage.BodyShape"));
        _stocky.GroupName = _slim.GroupName = "JaliumModelPreviewBodyShape";
        _stocky.IsChecked = true;
        _stocky.Margin = new Thickness(0, 0, 8, 0);
        _stocky.Checked += (_, _) => RenderSelection();
        _slim.Checked += (_, _) => RenderSelection();
        shape.Children.Add(_stocky);
        shape.Children.Add(_slim);
        groups.Children.Add(ToolGroup(shape));
        var armor = new StackPanel { Orientation = Orientation.Horizontal };
        _armorHeading.Foreground = Secondary;
        _armorHeading.VerticalAlignment = VerticalAlignment.Center;
        _armorHeading.Margin = new Thickness(0, 0, 8, 0);
        armor.Children.Add(_armorHeading);
        _armor.MinWidth = 190;
        _armor.MaxDropDownHeight = 300;
        _armor.DisplayMemberPath = "DisplayName";
        _armor.SelectionChanged += (_, _) => RenderSelection();
        armor.Children.Add(_armor);
        groups.Children.Add(ToolGroup(armor));
        Grid.SetRow(groups, 1);
        controls.Children.Add(groups);
        right.Children.Add(Card(controls, 12));
    }

    private void BuildViewport(Grid right)
    {
        var viewport = new Grid();
        viewport.Children.Add(_scene);
        _loadingOverlay.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x25, 0x2A));
        _loadingOverlay.Visibility = Visibility.Collapsed;
        var loading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        loading.Children.Add(new ProgressBar { Width = 180, Height = 4, IsIndeterminate = true });
        var loadingText = new TextBlock { Foreground = Secondary,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0) };
        loadingText.SetBinding(TextBlock.TextProperty, new Binding { Source = _status, Path = new PropertyPath("Text") });
        loading.Children.Add(loadingText);
        _loadingOverlay.Child = loading;
        viewport.Children.Add(_loadingOverlay);
        var frame = new Border { Child = viewport, Background = Paint(0x20, 0x25, 0x2A),
            BorderBrush = Stroke, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        Grid.SetRow(frame, 1);
        right.Children.Add(frame);
    }

    private void BuildTabs(Grid right)
    {
        var tabs = new TabControl { Background = Surface, Foreground = Foreground,
            BorderBrush = Stroke, BorderThickness = new Thickness(0) };
        var optionsArea = new Grid { Margin = new Thickness(4) };
        _noOptions.Foreground = Secondary;
        _noOptions.HorizontalAlignment = HorizontalAlignment.Center;
        _noOptions.VerticalAlignment = VerticalAlignment.Center;
        _noOptions.TextWrapping = TextWrapping.Wrap;
        optionsArea.Children.Add(_noOptions);
        optionsArea.Children.Add(new ScrollViewer { Content = _optionRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _optionTab.Content = optionsArea;
        tabs.Items.Add(_optionTab);

        var meshes = new Grid { Margin = new Thickness(4) };
        meshes.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        meshes.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var meshControls = new StackPanel { Margin = new Thickness(2, 0, 2, 7) };
        var materialLine = new StackPanel { Orientation = Orientation.Horizontal };
        _automaticMaterials.IsChecked = true;
        _automaticMaterials.Margin = new Thickness(0, 0, 12, 0);
        _automaticMaterials.Checked += (_, _) => RenderSelection();
        _automaticMaterials.Unchecked += (_, _) => RenderSelection();
        materialLine.Children.Add(_automaticMaterials);
        _textureHeading.Foreground = Secondary;
        _textureHeading.VerticalAlignment = VerticalAlignment.Center;
        _textureHeading.Margin = new Thickness(0, 0, 6, 0);
        materialLine.Children.Add(_textureHeading);
        _textures.MinWidth = 250;
        _textures.DisplayMemberPath = "TextureIdText";
        _textures.SelectionChanged += (_, _) => _ = LoadSelectedTextureAsync();
        materialLine.Children.Add(_textures);
        meshControls.Children.Add(materialLine);
        var resolutionLine = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0) };
        _originalResolution.Checked += (_, _) => _ = ReloadTexturesAsync();
        _originalResolution.Unchecked += (_, _) => _ = ReloadTexturesAsync();
        resolutionLine.Children.Add(_originalResolution);
        _resolutionHint.Foreground = Secondary;
        _resolutionHint.FontSize = 11;
        _resolutionHint.Margin = new Thickness(8, 0, 0, 0);
        resolutionLine.Children.Add(_resolutionHint);
        meshControls.Children.Add(resolutionLine);
        var filters = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0) };
        _showFiltered.Checked += (_, _) => RenderSelection();
        _showFiltered.Unchecked += (_, _) => RenderSelection();
        filters.Children.Add(_showFiltered);
        _hiddenSummary.Foreground = Secondary;
        _hiddenSummary.FontSize = 11;
        _hiddenSummary.Margin = new Thickness(8, 0, 12, 0);
        filters.Children.Add(_hiddenSummary);
        _isolateMesh.Checked += (_, _) => RenderSelection();
        _isolateMesh.Unchecked += (_, _) => RenderSelection();
        filters.Children.Add(_isolateMesh);
        meshControls.Children.Add(filters);
        var forceLine = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0) };
        _forceDecode.Checked += (_, _) => PendingLoad = LoadSelectedModAsync(resetCamera: false);
        _forceDecode.Unchecked += (_, _) => PendingLoad = LoadSelectedModAsync(resetCamera: false);
        forceLine.Children.Add(_forceDecode);
        _forceDecodeHint.Foreground = Secondary;
        _forceDecodeHint.FontSize = 11;
        _forceDecodeHint.Margin = new Thickness(8, 0, 0, 0);
        forceLine.Children.Add(_forceDecodeHint);
        meshControls.Children.Add(forceLine);
        meshes.Children.Add(meshControls);
        _meshGrid.AutoGenerateColumns = false;
        _meshGrid.IsReadOnly = true;
        _meshGrid.CanUserAddRows = false;
        _meshGrid.FontSize = 11;
        _meshGrid.SelectionChanged += (_, _) =>
        {
            if (_changingSelection)
                return;
            _selectedMesh = _meshGrid.SelectedItem as ModelPreviewMesh;
            RenderSelection();
        };
        foreach (var (key, property) in new (string, string)[]
        {
            ("ModelPreviewPage.Status", "PreviewStatusText"),
            ("ModelPreviewPage.Patch", "PatchFile"),
            ("ConflictDetail.Unit", "UnitIdText"),
            ("ModelPreviewPage.MeshInfo", "MeshInfoIndex"),
            ("ModelPreviewPage.Stream", "StreamIndex"),
            ("ModelPreviewPage.Uv", "UvStatusText"),
            ("ModelPreviewPage.Size", "BoundsText"),
            ("ModelPreviewPage.Vertices", "VertexCount"),
            ("ModelPreviewPage.Triangles", "TriangleCount"),
        })
        {
            var column = new DataGridTextColumn { Header = _localization[key], Binding = new Binding(property) };
            _meshGrid.Columns.Add(column);
            _columnKeys[column] = key;
        }
        Grid.SetRow(_meshGrid, 1);
        meshes.Children.Add(_meshGrid);
        _meshTab.Content = meshes;
        tabs.Items.Add(_meshTab);
        var tabFrame = Card(tabs, 6);
        Grid.SetRow(tabFrame, 3);
        right.Children.Add(tabFrame);
    }

    private Border ToolGroup(UIElement child) => new()
    {
        Child = child, Background = Paint(0x20, 0x20, 0x20), BorderBrush = Stroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 8),
    };

    private TextBlock Label(string key) => new()
    {
        Text = _localization[key], Foreground = Secondary,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
    };

    private static Border Card(UIElement child, double padding) => new()
    {
        Child = child, Background = Surface, BorderBrush = Stroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
        Padding = new Thickness(padding),
    };

    private void RefreshTexts()
    {
        _title.Text = _localization["ModelPreviewPage.Title"];
        _description.Text = _localization["ModelPreviewPage.Description"];
        _backButton.ToolTip = _localization["Common.Back"];
        _resourcesButton.Content = _localization["PatchResourceViewerPage.Title"];
        _refreshButton.Content = _localization["ModelPreviewPage.Refresh"];
        _modsHeading.Text = _localization["ModelPreviewPage.Mods"];
        _partsHint.Text = _localization["ModelPreviewPage.PartsHint"];
        _viewHeading.Text = _localization["ModelPreviewPage.ViewControls"];
        var views = new[] { "FrontView", "SideView", "TopView", "ResetView" };
        for (var index = 0; index < views.Length; index++)
            _cameraButtons[index].Content = _localization["ModelPreviewPage." + views[index]];
        RefreshAnimationTexts();
        _stocky.Content = _localization["ModelPreviewPage.StockyBody"];
        _slim.Content = _localization["ModelPreviewPage.SlimBody"];
        _armorHeading.Text = _localization["ModelPreviewPage.Armor"];
        _noOptions.Text = _localization["ModelPreviewPage.NoManifestOptions"];
        _optionTab.Header = _localization["ModelPreviewPage.PartsAndVariants"];
        _meshTab.Header = _localization["ModelPreviewPage.Meshes"];
        _automaticMaterials.Content = _localization["ModelPreviewPage.AutomaticMaterials"];
        _textureHeading.Text = _localization["ModelPreviewPage.Texture"];
        _originalResolution.Content = _localization["ModelPreviewPage.OriginalTextureResolution"];
        _resolutionHint.Text = _localization["ModelPreviewPage.OriginalTextureResolutionTip"];
        _showFiltered.Content = _localization["ModelPreviewPage.ShowFilteredMeshes"];
        _isolateMesh.Content = _localization["ModelPreviewPage.IsolateMesh"];
        _forceDecode.Content = _localization["ModelPreviewPage.ForceDecodeOversizedStreams"];
        _forceDecodeHint.Text = _localization["ModelPreviewPage.ForceDecodeOversizedStreamsTip"];
        foreach (var (column, key) in _columnKeys)
            column.Header = _localization[key];
        RefreshHiddenSummary();
    }

    private void OnLocalizationChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs args) => RefreshTexts();

    private static Brush Paint(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadCancellation?.Cancel();
        DisposeAnimation();
        _scene.Dispose();
        _localization.PropertyChanged -= OnLocalizationChanged;
    }
}
