using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Controls.Primitives;
using Jalium.UI.Data;
using Jalium.UI.Input;
using Jalium.UI.Media;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;

namespace Helldivers2ModManager.Jalium.Host;

internal enum DashboardAction
{
    AddMod, CreateMod, NexusDownload, BackgroundTasks, Help, ArmorReuse, PatchResourceViewer, Bisect,
    TagManagement, Settings, DeploymentOrder, CheckVersion, ScanConflicts,
    ConflictDetail, VersionDetail,
    Github, GithubFork, Discord, BatchTag, AddToGroup, DeleteMod, EditMod, EditManifest, PreviewModel,
    BatchRepair, Purge, Deploy, LaunchGame, Rescan,
    MoveToTop, MoveToBottom, MoveToPosition, OpenFileLocation,
    EditTags, RemoveFromGroup, EditName, EditDescription, EditImage, OpenModLink, EditLink,
    ExportMod, UpdateMod,
    CreateSeparator, RenameSeparator, ChangeSeparatorColor, DeleteSeparator,
}

internal interface IDashboardActionHandler
{
    Task ExecuteAsync(DashboardAction action, ModData? mod = null);
    Task ExecuteSeparatorAsync(DashboardAction action, ModSeparator separator)
        => Task.CompletedTask;
}

internal sealed class DashboardPageView : Grid, IDisposable
{
    private static double _savedScrollOffset;
    private static readonly SemaphoreSlim IconDecodeGate = new(4, 4);
    private static readonly Brush Foreground = Brush(0xFF, 0xFF, 0xFF);
    private static readonly Brush Secondary = Brush(0xB3, 0xB3, 0xB3);
    private static readonly Brush Stroke = Brush(0x3A, 0x3A, 0x3A);
    private readonly Brush Card;
    private static readonly Brush Elevated = Brush(0x32, 0x32, 0x32);
    private static readonly Brush Accent = Brush(0x00, 0x78, 0xD4);

    private readonly DashboardWorkspace _workspace;
    private readonly LocalizationService _localization;
    private readonly IDashboardActionHandler _actions;
    private readonly Action<Exception> _reportError;
    private readonly Action<ImageSource>? _previewImage;
    private readonly Dictionary<Button, string> _localizedButtons = [];
    private readonly Dictionary<string, FrameworkElement> _tutorialTargets = [];
    private readonly Dictionary<Guid, ImageSource?> _icons = [];
    private readonly Dictionary<(Guid Guid, int Width), Task<ImageSource?>> _iconLoads = [];
    private readonly ImageSource? _defaultModIcon;
    private readonly List<WeakReference<Border>> _conflictBadges = [];
    private readonly List<WeakReference<Border>> _versionBadges = [];
    private readonly Dictionary<Guid, IReadOnlyList<ModConflictRecord>> _conflictsByMod = [];
    private IReadOnlyDictionary<Guid, ModVersionCheckResult> _versionResults =
        new Dictionary<Guid, ModVersionCheckResult>();
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly TextBlock _searchHint;
    private readonly WrapPanel _batchBar;
    private readonly TextBlock _selectionCount;
    private readonly Button _workspaceButton;
    private readonly Button _libraryButton;
    private readonly Button _batchEnable;
    private readonly Button _batchDisable;
    private readonly Border _groupDrawer;
    private readonly StackPanel _groupButtons;
    private readonly TextBlock _selectedGroupName;
    private readonly TextBox _newGroupName;
    private readonly TextBlock _newGroupHint;
    private readonly Button _createGroupButton;
    private readonly Button _removeGroupButton;
    private readonly Button _deleteGroupButton;
    private readonly Button _groupToggle;
    private Button _conflictScanButton = null!;
    private Button _versionCheckButton = null!;
    private Button _batchRepairButton = null!;
    private bool _conflictScanCompleted;
    private bool _versionScanning;
    private ModData? _dragMod;
    private ModData? _selectionAnchor;
    private Point _dragStart;
    private bool _updatingSelection;
    private bool _suppressNativeSelection;
    private bool _disposed;
    private ScrollViewer? _listScrollViewer;

    public DashboardPageView(DashboardWorkspace workspace, LocalizationService localization,
        IDashboardActionHandler actions, Action<Exception> reportError, Action<ImageSource>? previewImage = null)
    {
        _workspace = workspace;
        _localization = localization;
        _actions = actions;
        _reportError = reportError;
        _previewImage = previewImage;
        _defaultModIcon = TryLoadDefaultIcon();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Card = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(workspace.CardOpacity * 255), 0x2E, 0x2E, 0x2E));
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nav = BuildNavigation();
        Grid.SetRowSpan(nav, 3);
        Children.Add(nav);

        var top = new Border
        {
            Background = Card, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var topGrid = new Grid();
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchArea = new Grid();
        searchArea.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _search = new TextBox { Height = 40, Margin = new Thickness(0, 0, 8, 0),
            Text = workspace.SearchText, VerticalAlignment = VerticalAlignment.Center };
        _searchHint = new TextBlock { Foreground = Secondary, IsHitTestVisible = false,
            Margin = new Thickness(12, 0, 52, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
            _workspace.SetSearchText(_search.Text);
        };
        searchArea.Children.Add(_search);
        searchArea.Children.Add(_searchHint);
        var clearSearch = IconButton("\u00D7", "DashboardPage.ClearSearch", () => _search.Text = string.Empty);
        if (clearSearch.Content is TextBlock clearIcon)
            clearIcon.FontFamily = new FontFamily("Segoe UI");
        clearSearch.Margin = new Thickness(0, 0, 16, 0);
        Grid.SetColumn(clearSearch, 1);
        searchArea.Children.Add(clearSearch);
        topGrid.Children.Add(searchArea);

        var modes = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _workspaceButton = CommandButton("ModGroup.WorkspaceView", () => _workspace.SetMode(DashboardCatalogMode.Workspace));
        _libraryButton = CommandButton("ModGroup.LibraryView", () => _workspace.SetMode(DashboardCatalogMode.Library));
        _workspaceButton.Width = _libraryButton.Width = 112;
        _workspaceButton.Height = _libraryButton.Height = 40;
        _libraryButton.Margin = new Thickness(6, 0, 0, 0);
        modes.Children.Add(_workspaceButton);
        modes.Children.Add(_libraryButton);
        Grid.SetColumn(modes, 1);
        topGrid.Children.Add(modes);

        _batchBar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var selectionBorder = new Border { Background = Brush(0x3B, 0x3B, 0x3B),
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 6) };
        _selectionCount = new TextBlock { FontSize = 13, Foreground = Secondary };
        selectionBorder.Child = _selectionCount;
        _batchBar.Children.Add(selectionBorder);
        AddBatch("DashboardPage.SelectAll", _workspace.SelectAll);
        AddBatch("DashboardPage.DeselectAll", _workspace.DeselectAll);
        AddBatch("DashboardPage.InvertSelection", _workspace.InvertSelection);
        _batchEnable = AddBatch("DashboardPage.BatchEnable", () => RunAsync(() => _workspace.SetSelectedEnabledAsync(true)));
        _batchDisable = AddBatch("DashboardPage.BatchDisable", () => RunAsync(() => _workspace.SetSelectedEnabledAsync(false)));
        AddBatch("DashboardPage.BatchTag", () => Dispatch(DashboardAction.BatchTag));
        AddBatch("ModGroup.AddToGroup", () => Dispatch(DashboardAction.AddToGroup));
        AddBatch("Common.Delete", () => Dispatch(DashboardAction.DeleteMod));
        Grid.SetRow(_batchBar, 1);
        Grid.SetColumnSpan(_batchBar, 2);
        topGrid.Children.Add(_batchBar);
        top.Child = topGrid;
        _tutorialTargets["TopActionBar"] = top;
        Grid.SetColumn(top, 1);
        Children.Add(top);

        var rowTemplate = new DataTemplate();
        rowTemplate.SetVisualTree(CreateDashboardRow);
        _list = new ListBox
        {
            ItemsSource = _workspace.Rows,
            ItemTemplate = rowTemplate,
            ItemContainerStyle = CreateModItemStyle(),
            SelectionMode = SelectionMode.Extended,
            AllowDrop = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 16),
        };
        var panelTemplate = new ItemsPanelTemplate();
        panelTemplate.SetVisualTree(() => new VirtualizingStackPanel());
        _list.ItemsPanel = panelTemplate;
        _list.SelectionChanged += (_, _) => SyncSelectionFromList();
        _list.PreviewMouseLeftButtonDown += OnMouseDown;
        _list.PreviewMouseMove += OnMouseMove;
        _list.MouseRightButtonDown += (_, eventArgs) =>
        {
            if (_workspace.ShowSeparator && FindItem(eventArgs.OriginalSource as DependencyObject) is null)
            {
                Dispatch(DashboardAction.CreateSeparator);
                eventArgs.Handled = true;
            }
        };
        _list.DragOver += OnDragOver;
        _list.Drop += OnDrop;
        Grid.SetRow(_list, 1);
        Grid.SetColumn(_list, 1);
        Children.Add(_list);

        _groupToggle = IconButton("\u2630", "ModGroup.ToggleSidebar", ToggleGroupDrawer);
        if (_groupToggle.Content is TextBlock groupIcon)
            groupIcon.FontFamily = new FontFamily("Segoe UI");
        _groupToggle.Margin = new Thickness(8, 0, 8, 0);
        _groupToggle.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetRow(_groupToggle, 1);
        Grid.SetColumn(_groupToggle, 2);
        Children.Add(_groupToggle);

        var bottom = new Border { Background = Card, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16) };
        var bottomActions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        _tutorialTargets["BottomActionButtons"] = bottomActions;
        _batchRepairButton = AddBottom(bottomActions, "VersionCheckBatch.Button", DashboardAction.BatchRepair);
        _batchRepairButton.Visibility = _workspace.EnableBatchRepair ? Visibility.Visible : Visibility.Collapsed;
        var rescan = CommandButton("DashboardPage.RescanMods",
            () => Dispatch(DashboardAction.Rescan));
        bottomActions.Children.Add(rescan);
        AddBottom(bottomActions, "DashboardPage.CleanMods", DashboardAction.Purge);
        AddBottom(bottomActions, "DashboardPage.DeployMods", DashboardAction.Deploy, primary: true);
        AddBottom(bottomActions, "DashboardPage.LaunchGame", DashboardAction.LaunchGame);
        bottom.Child = bottomActions;
        Grid.SetRow(bottom, 2);
        Grid.SetColumn(bottom, 1);
        Children.Add(bottom);

        _groupButtons = new StackPanel();
        _selectedGroupName = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Accent, Margin = new Thickness(0, 2, 0, 12) };
        _newGroupName = new TextBox { Height = 36, Margin = new Thickness(0, 0, 0, 8) };
        _newGroupHint = new TextBlock { Foreground = Secondary, IsHitTestVisible = false,
            Margin = new Thickness(12, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        _newGroupName.TextChanged += (_, _) => _newGroupHint.Visibility =
            string.IsNullOrEmpty(_newGroupName.Text) ? Visibility.Visible : Visibility.Collapsed;
        _createGroupButton = CommandButton("ModGroup.CreateGroup", () => RunAsync(async () =>
        {
            await _workspace.CreateGroupAsync(_newGroupName.Text);
            _newGroupName.Text = string.Empty;
        }));
        _removeGroupButton = CommandButton("ModGroup.RemoveSelected",
            () => RunAsync(_workspace.RemoveSelectedFromCurrentGroupAsync));
        _deleteGroupButton = CommandButton("ModGroup.DeleteGroup",
            () => RunAsync(() => _workspace.DeleteGroupAsync(_workspace.Groups.SelectedGroup.Id)));
        _groupDrawer = BuildGroupDrawer();
        _groupDrawer.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_groupDrawer, 1);
        Grid.SetRowSpan(_groupDrawer, 3);
        Panel.SetZIndex(_groupDrawer, 10);
        Children.Add(_groupDrawer);

        _workspace.Changed += OnWorkspaceChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RefreshState();
    }

    private Border BuildNavigation()
    {
        var root = new Border { Background = Elevated, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 16, 0) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var navButtons = new StackPanel();
        var addCreate = new StackPanel();
        AddNav(addCreate, "\uE710", "DashboardPage.AddMod", DashboardAction.AddMod);
        AddNav(addCreate, "\uE7AC", "DashboardPage.CreateMod", DashboardAction.CreateMod);
        AddNav(addCreate, "\u2193", "NexusDownloadPage.Title", DashboardAction.NexusDownload);
        navButtons.Children.Add(addCreate);
        _tutorialTargets["AddCreatePanel"] = addCreate;
        AddNav(navButtons, "\uE9F5", "DashboardPage.BackgroundTasks", DashboardAction.BackgroundTasks);
        AddNav(navButtons, "\uE7BA", "DashboardPage.ArmorReuse", DashboardAction.ArmorReuse);
        AddNav(navButtons, "\uE9D9", "DashboardPage.PatchResourceViewer", DashboardAction.PatchResourceViewer);
        AddNav(navButtons, "\uE9E9", "DashboardPage.Bisect", DashboardAction.Bisect);
        AddNav(navButtons, "\uE8EC", "DashboardPage.TagManagement", DashboardAction.TagManagement);
        AddNav(navButtons, "\uE713", "DashboardPage.Settings", DashboardAction.Settings);
        var deploymentOrder = AddNav(navButtons, "\uE718", "DashboardPage.DeploymentOrder", DashboardAction.DeploymentOrder);
        deploymentOrder.Visibility = _workspace.UseDeploymentOrder ? Visibility.Visible : Visibility.Collapsed;
        var scroll = new ScrollViewer { Content = navButtons,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        layout.Children.Add(scroll);

        var checks = new StackPanel();
        _tutorialTargets["VersionCheckPanel"] = checks;
        _versionCheckButton = AddNav(checks, "\uE897", "DashboardPage.CheckVersion", DashboardAction.CheckVersion);
        _conflictScanButton = AddNav(checks, "\uE7C4", "DashboardPage.ScanConflicts", DashboardAction.ScanConflicts);
        Grid.SetRow(checks, 1);
        layout.Children.Add(checks);
        var links = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var github = ImageButton("github_icon.png", "DashboardPage.GitHub", null);
        var githubPopup = CreateGithubPopup(github);
        github.Click += (_, _) =>
        {
            githubPopup.PlacementTarget = github;
            githubPopup.IsOpen = true;
        };
        links.Children.Add(github);
        links.Children.Add(ImageButton("discord_icon.png", "DashboardPage.Discord", DashboardAction.Discord));
        Grid.SetRow(links, 2);
        layout.Children.Add(links);
        root.Child = layout;
        return root;
    }

    private Border BuildGroupDrawer()
    {
        var drawer = new Border { Width = 260, HorizontalAlignment = HorizontalAlignment.Right,
            Background = Elevated, BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12) };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = _localization["ModGroup.SidebarTitle"],
            FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Foreground,
            Margin = new Thickness(0, 0, 0, 12) });
        header.Children.Add(new TextBlock { Text = _localization["ModGroup.CurrentGroup"],
            FontSize = 12, Foreground = Secondary });
        header.Children.Add(_selectedGroupName);
        layout.Children.Add(header);
        var groupsScroll = new ScrollViewer { Content = _groupButtons,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(groupsScroll, 1);
        layout.Children.Add(groupsScroll);
        var commands = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var newGroupInput = new Grid();
        newGroupInput.Children.Add(_newGroupName);
        newGroupInput.Children.Add(_newGroupHint);
        commands.Children.Add(newGroupInput);
        commands.Children.Add(_createGroupButton);
        commands.Children.Add(_removeGroupButton);
        commands.Children.Add(_deleteGroupButton);
        Grid.SetRow(commands, 2);
        layout.Children.Add(commands);
        drawer.Child = layout;
        return drawer;
    }

    private FrameworkElement CreateDashboardRow()
    {
        var host = new Border();
        void UpdateRow()
        {
            host.Child = host.DataContext switch
            {
                ModData => CreateModCard(),
                ModSeparator => CreateSeparator(),
                _ => null,
            };
        }
        host.DataContextChanged += (_, _) => UpdateRow();
        host.Loaded += (_, _) => UpdateRow();
        return host;
    }

    private static Style CreateModItemStyle()
    {
        var template = new ControlTemplate(typeof(ListBoxItem));
        template.SetVisualTree(() => new Border
        {
            Name = "SelectionBorder",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Child = new ContentPresenter(),
        });
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "SelectionBorder"));
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private FrameworkElement CreateModCard()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Image { Width = 60, Height = 60, Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 12, 0) };
        var tooltipImage = new Image { Width = 300, Height = 300, Stretch = Stretch.Uniform };
        icon.ToolTip = tooltipImage;
        icon.ToolTipOpening += async (_, _) =>
        {
            if (icon.DataContext is not ModData mod)
                return;
            tooltipImage.Source = await LoadIconAsync(mod, 1024);
        };
        icon.MouseLeftButtonDown += async (_, e) =>
        {
            if (_previewImage is null || icon.DataContext is not ModData mod)
                return;
            var guid = mod.Manifest.Guid;
            var source = await LoadIconAsync(mod, 1024);
            if (source is not null && icon.DataContext is ModData current
                && current.Manifest.Guid == guid)
                _previewImage(source);
            e.Handled = true;
        };
        var iconLayer = new Grid { Width = 60, Height = 60 };
        iconLayer.Children.Add(icon);
        var conflictIcon = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = Foreground, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        var conflictBadge = new Border { Child = conflictIcon, Width = 18, Height = 18,
            CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, -9, 0, 0),
            Visibility = Visibility.Collapsed };
        conflictBadge.DataContextChanged += (_, _) => UpdateConflictBadge(conflictBadge);
        conflictBadge.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (conflictBadge.DataContext is ModData mod)
            {
                Dispatch(DashboardAction.ConflictDetail, mod);
                eventArgs.Handled = true;
            }
        };
        _conflictBadges.Add(new WeakReference<Border>(conflictBadge));
        iconLayer.Children.Add(conflictBadge);
        var versionBadge = new Border { Width = 14, Height = 14,
            CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -7, 6, 0),
            Visibility = Visibility.Collapsed };
        versionBadge.DataContextChanged += (_, _) => UpdateVersionBadge(versionBadge);
        versionBadge.MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (versionBadge.DataContext is ModData mod)
            {
                Dispatch(DashboardAction.VersionDetail, mod);
                eventArgs.Handled = true;
            }
        };
        _versionBadges.Add(new WeakReference<Border>(versionBadge));
        iconLayer.Children.Add(versionBadge);
        row.Children.Add(iconLayer);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = Foreground, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        name.SetBinding(TextBlock.TextProperty, "Manifest.Name");
        nameRow.Children.Add(name);
        var openLink = IconButton("\uE71B", "DashboardPage.OpenModLink", null);
        openLink.Width = 30;
        openLink.Height = 30;
        openLink.Click += (_, _) =>
        {
            if (openLink.DataContext is ModData mod)
                Dispatch(DashboardAction.OpenModLink, mod);
        };
        Grid.SetColumn(openLink, 1);
        nameRow.Children.Add(openLink);
        text.Children.Add(nameRow);
        var tags = new TextBlock { FontSize = 11, Foreground = Accent,
            TextTrimming = TextTrimming.CharacterEllipsis };
        text.Children.Add(tags);
        var description = new TextBlock { FontSize = 13, Foreground = Secondary,
            TextTrimming = TextTrimming.WordEllipsis };
        description.SetBinding(TextBlock.TextProperty, "Manifest.Description");
        text.Children.Add(description);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var optionActions = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        var options = new ComboBox { Width = 140, Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center };
        var settingOptions = false;
        options.SelectionChanged += (_, _) =>
        {
            if (!settingOptions && options.DataContext is ModData mod && options.SelectedIndex >= 0)
                RunAsync(() => _workspace.SetLegacyOptionAsync(mod, options.SelectedIndex));
        };
        optionActions.Children.Add(options);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center };
        var edit = CommandButton("Common.Edit", null);
        edit.Padding = new Thickness(12, 4, 12, 4);
        edit.Click += (_, _) =>
        {
            if (edit.DataContext is ModData mod)
                Dispatch(DashboardAction.EditMod, mod);
        };
        editActions.Children.Add(edit);
        var manifest = CommandButton("DashboardPage.EditManifest", null);
        manifest.Padding = new Thickness(12, 4, 12, 4);
        manifest.Margin = new Thickness(6, 0, 0, 0);
        manifest.Click += (_, _) =>
        {
            if (manifest.DataContext is ModData mod)
                Dispatch(DashboardAction.EditManifest, mod);
        };
        editActions.Children.Add(manifest);
        var more = IconButton("\uE712", "DashboardPage.MoreActions", null);
        more.Margin = new Thickness(6, 0, 0, 0);
        Border? cardTarget = null;
        var actionPopup = CreateModActionsPopup(() => cardTarget?.DataContext as ModData);
        more.Click += (_, _) =>
        {
            if (more.DataContext is ModData mod)
            {
                actionPopup.PlacementTarget = cardTarget;
                actionPopup.IsOpen = true;
            }
        };
        editActions.Children.Add(more);
        optionActions.Children.Add(editActions);
        Grid.SetColumn(optionActions, 2);
        row.Children.Add(optionActions);

        var enabled = new CheckBox { Width = 24, Height = 24,
            Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        enabled.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(ModData.Enabled)));
        enabled.Click += (_, _) =>
        {
            if (enabled.DataContext is ModData mod)
                RunAsync(() => _workspace.SetEnabledAsync(mod, enabled.IsChecked == true));
        };
        Grid.SetColumn(enabled, 3);
        row.Children.Add(enabled);
        var add = CommandButton("ModGroup.AddToGroup", null);
        add.Width = 112;
        add.Height = 40;
        add.Click += (_, _) =>
        {
            if (add.DataContext is ModData mod)
                Dispatch(DashboardAction.AddToGroup, mod);
        };
        Grid.SetColumn(add, 3);
        row.Children.Add(add);
        var remove = IconButton("\u00D7", "DashboardPage.DeleteModHint", null);
        if (remove.Content is TextBlock removeIcon)
            removeIcon.FontFamily = new FontFamily("Segoe UI");
        remove.Click += (_, _) =>
        {
            if (remove.DataContext is ModData mod)
                Dispatch(DashboardAction.DeleteMod, mod);
        };
        Grid.SetColumn(remove, 4);
        row.Children.Add(remove);
        var position = new TextBlock { FontSize = 11, Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(position, 5);
        row.Children.Add(position);

        var card = new Border { Child = row, Background = Card,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16),
            Margin = new Thickness(0, 4, 0, 4) };
        cardTarget = card;
        card.MouseRightButtonDown += (_, eventArgs) =>
        {
            if (card.DataContext is not ModData)
                return;
            actionPopup.PlacementTarget = card;
            actionPopup.IsOpen = true;
            eventArgs.Handled = true;
        };
        card.Loaded += (_, _) =>
        {
            if (card.DataContext is not ModData mod)
                return;
            icon.DataContext = mod;
            if (_icons.TryGetValue(mod.Manifest.Guid, out var source))
                icon.Source = source;
            else
            {
                icon.Source = _defaultModIcon;
                _ = LoadCardIconAsync(mod, icon);
            }
            UpdateConflictBadge(conflictBadge);
            UpdateVersionBadge(versionBadge);
            tags.Text = string.Join("  ", _workspace.GetTags(mod).Select(tag => tag.Name));
            position.Text = _workspace.GetPosition(mod).ToString();
            var legacy = mod.Manifest is LegacyModManifest { Options: { Count: > 0 } };
            options.Visibility = legacy && _workspace.Mode == DashboardCatalogMode.Workspace
                ? Visibility.Visible : Visibility.Collapsed;
            if (legacy)
            {
                settingOptions = true;
                options.ItemsSource = ((LegacyModManifest)mod.Manifest).Options;
                options.SelectedIndex = mod.SelectedOptions[0];
                settingOptions = false;
            }
            edit.Visibility = mod.Manifest is V1ModManifest && _workspace.Mode == DashboardCatalogMode.Workspace
                ? Visibility.Visible : Visibility.Collapsed;
            more.Visibility = Visibility.Visible;
            enabled.Visibility = _workspace.Mode == DashboardCatalogMode.Workspace
                ? Visibility.Visible : Visibility.Collapsed;
            add.Visibility = _workspace.Mode == DashboardCatalogMode.Library
                ? Visibility.Visible : Visibility.Collapsed;
        };
        return card;
    }

    private Popup CreateModActionsPopup(Func<ModData?> getMod)
    {
        var actions = new StackPanel { Width = 250 };
        var popup = new Popup
        {
            Child = new Border { Background = Elevated, BorderBrush = Stroke,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6), Child = actions },
            Placement = PlacementMode.Right,
            StaysOpen = false,
        };
        void Add(string key, DashboardAction action, bool enabled = true)
        {
            var button = new Button { Content = _localization[key], Height = 34,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4), IsEnabled = enabled,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            button.Click += (_, _) =>
            {
                popup.IsOpen = false;
                if (getMod() is { } mod)
                    Dispatch(action, mod);
            };
            actions.Children.Add(button);
        }
        Add("DashboardPage.PreviewModel", DashboardAction.PreviewModel);
        Add("DashboardPage.MoveToTop", DashboardAction.MoveToTop,
            _workspace.Mode == DashboardCatalogMode.Workspace && string.IsNullOrEmpty(_workspace.SearchText));
        Add("DashboardPage.MoveToBottom", DashboardAction.MoveToBottom,
            _workspace.Mode == DashboardCatalogMode.Workspace && string.IsNullOrEmpty(_workspace.SearchText));
        Add("DashboardPage.MoveToPosition", DashboardAction.MoveToPosition,
            _workspace.Mode == DashboardCatalogMode.Workspace && string.IsNullOrEmpty(_workspace.SearchText));
        Add("DashboardPage.OpenFileLocation", DashboardAction.OpenFileLocation);
        Add("DashboardPage.SetTags", DashboardAction.EditTags);
        Add("ModGroup.AddToGroup", DashboardAction.AddToGroup);
        Add("ModGroup.RemoveFromGroup", DashboardAction.RemoveFromGroup,
            _workspace.Mode == DashboardCatalogMode.Workspace);
        Add("DashboardPage.EditNameTitle", DashboardAction.EditName);
        Add("DashboardPage.EditDescTitle", DashboardAction.EditDescription);
        Add("DashboardPage.EditImageDialog", DashboardAction.EditImage);
        Add("DashboardPage.EditLink", DashboardAction.EditLink);
        Add("DashboardPage.EditManifest", DashboardAction.EditManifest);
        Add("DashboardPage.PackageExport", DashboardAction.ExportMod);
        Add("DashboardPage.UpdateMod", DashboardAction.UpdateMod);
        Add("Common.Delete", DashboardAction.DeleteMod);
        return popup;
    }

    internal void SetConflictResult(ModConflictAnalysisResult? result)
    {
        _conflictsByMod.Clear();
        _conflictScanCompleted = result is not null;
        if (result is not null)
        {
            foreach (var group in result.Conflicts
                         .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.FriendlyName))
                         .SelectMany(conflict => conflict.Participants
                             .Select(participant => (participant.ModGuid, Conflict: conflict)))
                         .GroupBy(static item => item.ModGuid))
                _conflictsByMod[group.Key] = group.Select(static item => item.Conflict).Distinct().ToArray();
        }
        UpdateConflictBadges();
    }

    internal void SetConflictScanning(bool scanning) => _conflictScanButton.IsEnabled = !scanning;

    internal void SetVersionResults(IReadOnlyDictionary<Guid, ModVersionCheckResult> results)
    {
        _versionResults = results;
        foreach (var reference in _versionBadges)
            if (reference.TryGetTarget(out var badge))
                UpdateVersionBadge(badge);
    }

    internal void SetVersionScanning(bool scanning)
    {
        _versionScanning = scanning;
        _versionCheckButton.IsEnabled = !scanning;
        foreach (var reference in _versionBadges)
            if (reference.TryGetTarget(out var badge))
                badge.Visibility = scanning ? Visibility.Collapsed : Visibility.Visible;
        if (!scanning)
            SetVersionResults(_versionResults);
    }

    private void UpdateVersionBadge(Border badge)
    {
        if (_versionScanning || badge.DataContext is not ModData mod
            || !_versionResults.TryGetValue(mod.Manifest.Guid, out var result)
            || result.Status == ModVersionStatus.Unknown)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }
        badge.Visibility = Visibility.Visible;
        badge.Background = result.Status == ModVersionStatus.Compatible
            ? Brush(0x28, 0xA0, 0x5F) : Brush(0xDC, 0x50, 0x37);
        var statusKey = result.Status switch
        {
            ModVersionStatus.Compatible => "Converters.Compatible",
            ModVersionStatus.Incompatible => "Converters.Incompatible",
            _ => "VersionCheck.CheckFailed",
        };
        badge.ToolTip = _localization["DashboardPage.VersionCompatibility"] + _localization[statusKey]
            + "\n" + _localization["DashboardPage.ClickForDetails"];
    }

    private void UpdateConflictBadges()
    {
        for (var index = _conflictBadges.Count - 1; index >= 0; index--)
            if (_conflictBadges[index].TryGetTarget(out var badge))
                UpdateConflictBadge(badge);
            else
                _conflictBadges.RemoveAt(index);
    }

    private void UpdateConflictBadge(Border badge)
    {
        if (!_conflictScanCompleted || badge.DataContext is not ModData mod)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }
        var hasConflict = _conflictsByMod.ContainsKey(mod.Manifest.Guid);
        badge.Visibility = Visibility.Visible;
        badge.Background = hasConflict ? Brush(0xDC, 0x50, 0x37) : Brush(0x28, 0xA0, 0x5F);
        badge.ToolTip = _localization["DashboardPage.ConflictStatusHint"];
        ((TextBlock)badge.Child!).Text = hasConflict ? "!" : "\u2713";
    }

    private FrameworkElement CreateSeparator()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Foreground, Margin = new Thickness(4, 0, 0, 0) };
        name.SetBinding(TextBlock.TextProperty, nameof(ModSeparator.Name));
        row.Children.Add(name);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var rename = IconButton("\uE8AC", "DashboardPage.RenameSeparatorTitle", null);
        rename.Click += (_, _) =>
        {
            if (rename.DataContext is ModSeparator separator)
                RunSeparatorAsync(DashboardAction.RenameSeparator, separator);
        };
        actions.Children.Add(rename);
        var color = IconButton("\uE790", "DashboardPage.ChangeSeparatorColorTitle", null);
        color.Click += (_, _) =>
        {
            if (color.DataContext is ModSeparator separator)
                RunSeparatorAsync(DashboardAction.ChangeSeparatorColor, separator);
        };
        actions.Children.Add(color);
        var remove = IconButton("\uE74D", "DashboardPage.DeleteSeparatorHint", null);
        remove.Click += (_, _) =>
        {
            if (remove.DataContext is ModSeparator separator)
                RunSeparatorAsync(DashboardAction.DeleteSeparator, separator);
        };
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        var border = new Border { Child = row, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 2, 0, 2), CornerRadius = new CornerRadius(6) };
        border.Loaded += (_, _) =>
        {
            if (border.DataContext is ModSeparator separator)
                border.Background = ParseColor(separator.Color);
        };
        return border;
    }

    private async void RunSeparatorAsync(DashboardAction action, ModSeparator separator)
    {
        try { await _actions.ExecuteSeparatorAsync(action, separator); }
        catch (Exception ex) { _reportError(ex); }
    }

    private async Task LoadCardIconAsync(ModData mod, Image icon)
    {
        var guid = mod.Manifest.Guid;
        try
        {
            var source = await LoadIconAsync(mod, 128);
            source ??= _defaultModIcon;
            _icons[guid] = source;
            if (!_disposed && icon.DataContext is ModData current && current.Manifest.Guid == guid)
                icon.Source = source;
        }
        catch (Exception ex)
        {
            _reportError(ex);
        }
    }

    private Task<ImageSource?> LoadIconAsync(ModData mod, int decodeWidth)
    {
        var key = (mod.Manifest.Guid, decodeWidth);
        if (decodeWidth == 128 && _icons.TryGetValue(key.Guid, out var cached))
            return Task.FromResult(cached);
        if (_iconLoads.TryGetValue(key, out var pending))
            return pending;

        var path = ResolveIconPath(mod);
        if (path is null)
            return Task.FromResult(_defaultModIcon);

        var task = CompleteIconLoadAsync(key, DecodeIconAsync(path, decodeWidth));
        _iconLoads[key] = task;
        return task;
    }

    private async Task<ImageSource?> CompleteIconLoadAsync(
        (Guid Guid, int Width) key, Task<ImageSource?> task)
    {
        try { return await task ?? _defaultModIcon; }
        finally { _iconLoads.Remove(key); }
    }

    private static async Task<ImageSource?> DecodeIconAsync(string path, int decodeWidth)
    {
        await IconDecodeGate.WaitAsync();
        try
        {
            return await Task.Run<ImageSource?>(() =>
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(path, UriKind.Absolute);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = decodeWidth;
                    image.EndInit();
                    if (image.CanFreeze)
                        image.Freeze();
                    return image;
                }
                catch (Exception)
                {
                    return null;
                }
            });
        }
        finally { IconDecodeGate.Release(); }
    }

    private static string? ResolveIconPath(ModData mod)
    {
        var relative = mod.Manifest.IconPath;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return null;
        try
        {
            var root = Path.GetFullPath(mod.Directory.FullName);
            var path = Path.GetFullPath(Path.Combine(root, relative));
            return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? TryLoadDefaultIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Images", "logo_icon.png");
            return File.Exists(path) ? BitmapImage.FromFile(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Button AddNav(StackPanel panel, string glyph, string key, DashboardAction action)
    {
        var button = CommandButton(key, () => Dispatch(action));
        button.BorderThickness = new Thickness(0);
        button.Background = Brushes.Transparent;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 2, 0, 2);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph,
            FontFamily = new FontFamily(glyph == "\u2193" ? "Segoe UI Symbol" : "Segoe Fluent Icons"),
            FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = _localization[key], Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        content.Children.Add(label);
        button.Content = content;
        panel.Children.Add(button);
        var tutorialName = action switch
        {
            DashboardAction.BackgroundTasks => "BackgroundTasksButton",
            DashboardAction.ArmorReuse => "ArmorReuseButton",
            DashboardAction.PatchResourceViewer => "PatchResourceViewerButton",
            DashboardAction.Bisect => "BisectButton",
            DashboardAction.TagManagement => "TagManagementButton",
            DashboardAction.Settings => "SettingsButton",
            _ => null,
        };
        if (tutorialName is not null)
            _tutorialTargets[tutorialName] = button;
        return button;
    }

    internal FrameworkElement? GetTutorialTarget(string name)
        => _tutorialTargets.GetValueOrDefault(name);

    private Button AddBatch(string key, Action action)
    {
        var button = CommandButton(key, action);
        button.Margin = new Thickness(0, 0, 6, 6);
        button.Padding = new Thickness(12, 6, 12, 6);
        _batchBar.Children.Add(button);
        return button;
    }

    private Button AddBottom(StackPanel panel, string key, DashboardAction action, bool primary = false)
    {
        var button = CommandButton(key, () => Dispatch(action));
        button.Margin = new Thickness(4, 0, 0, 0);
        button.Padding = new Thickness(20, 10, 20, 10);
        if (primary)
            button.Background = Accent;
        panel.Children.Add(button);
        return button;
    }

    private Button CommandButton(string key, Action? action)
    {
        var button = new Button { Content = _localization[key], MinHeight = 36,
            Foreground = Foreground, Background = Brushes.Transparent,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 12, 8), FontSize = 14 };
        if (action is not null)
            button.Click += (_, _) => action();
        _localizedButtons[button] = key;
        return button;
    }

    private Button IconButton(string glyph, string key, Action? action)
    {
        var button = new Button { Width = 36, Height = 36, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), ToolTip = _localization[key],
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16, Foreground = Foreground } };
        if (action is not null)
            button.Click += (_, _) => action();
        _localizedButtons[button] = key;
        return button;
    }

    private Button ImageButton(string file, string key, DashboardAction? action)
    {
        var button = IconButton(string.Empty, key, action is { } value ? () => Dispatch(value) : null);
        button.Content = new Image
        {
            Source = BitmapImage.FromFile(Path.Combine(AppContext.BaseDirectory, "Images", file)),
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
        };
        return button;
    }

    private Popup CreateGithubPopup(Button target)
    {
        var items = new StackPanel { Width = 220 };
        var popup = new Popup
        {
            Child = new Border { Background = Elevated, BorderBrush = Stroke,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6), Child = items },
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
        };
        foreach (var (key, action) in new[]
                 {
                     ("DashboardPage.OriginalRepo", DashboardAction.Github),
                     ("DashboardPage.ForkedRepo", DashboardAction.GithubFork),
                 })
        {
            var item = new Button { Content = _localization[key], Height = 34,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 4, 10, 4),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            _localizedButtons[item] = key;
            item.Click += (_, _) =>
            {
                popup.IsOpen = false;
                Dispatch(action);
            };
            items.Children.Add(item);
        }
        popup.PlacementTarget = target;
        return popup;
    }

    private void ToggleGroupDrawer() => _groupDrawer.Visibility = _groupDrawer.Visibility == Visibility.Visible
        ? Visibility.Collapsed : Visibility.Visible;

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        var isInteractive = e.OriginalSource is Control or Image;
        var item = FindItem(e.OriginalSource as DependencyObject);
        var mod = item?.DataContext as ModData;
        var modifiers = Keyboard.Modifiers;
        if (isInteractive || (mod is not null
            && !modifiers.HasFlag(ModifierKeys.Control)
            && !modifiers.HasFlag(ModifierKeys.Shift)))
            SuppressNativeSelectionForCurrentInput();
        if (HandleCardSelection(mod, isInteractive, modifiers))
            e.Handled = true;
        _dragMod = !isInteractive
            && _workspace.Mode == DashboardCatalogMode.Workspace
            && string.IsNullOrEmpty(_workspace.SearchText) ? mod : null;
        _dragStart = e.GetPosition(_list);
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_dragMod is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        var current = e.GetPosition(_list);
        if (Math.Abs(current.X - _dragStart.X) < 6 && Math.Abs(current.Y - _dragStart.Y) < 6)
            return;
        var source = _dragMod;
        _dragMod = null;
        DragDrop.DoDragDrop(_list, source, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (_workspace.Mode == DashboardCatalogMode.Workspace
            && string.IsNullOrEmpty(_workspace.SearchText)
            && e.Data.GetData(typeof(ModData)) is ModData)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            || e.Data.GetData(typeof(ModData)) is not ModData source
            || _workspace.Mode != DashboardCatalogMode.Workspace
            || !string.IsNullOrEmpty(_workspace.SearchText))
            return;
        var container = FindItem(e.OriginalSource as DependencyObject);
        var target = container?.DataContext as ModData;
        var visible = _workspace.Rows.OfType<ModData>().ToArray();
        var index = target is null ? visible.Length : Array.IndexOf(visible, target);
        if (container is not null && e.GetPosition(container).Y >= container.ActualHeight / 2)
            index++;
        RunAsync(() => _workspace.MoveAsync(source, index));
        e.Handled = true;
    }

    private static ListBoxItem? FindItem(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBoxItem item)
                return item;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            _listScrollViewer = FindDescendantScrollViewer(_list);
            if (_listScrollViewer is not null && _savedScrollOffset > 0)
                _listScrollViewer.ScrollToVerticalOffset(_savedScrollOffset);
        });
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => SaveScrollPosition();

    private void SaveScrollPosition()
    {
        if (_listScrollViewer is not null)
            _savedScrollOffset = _listScrollViewer.VerticalOffset;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is null)
                continue;
            if (child is ScrollViewer scrollViewer)
                return scrollViewer;
            if (FindDescendantScrollViewer(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void SyncSelectionFromList()
    {
        if (_updatingSelection)
            return;
        if (_suppressNativeSelection)
        {
            _updatingSelection = true;
            try
            {
                RefreshState();
            }
            finally
            {
                _updatingSelection = false;
                _suppressNativeSelection = false;
            }
            return;
        }
        _updatingSelection = true;
        try
        {
            _workspace.ReplaceVisibleSelection(_list.SelectedItems.OfType<ModData>()
                .Select(mod => mod.Manifest.Guid));
        }
        finally
        {
            _updatingSelection = false;
        }
    }

    private void SuppressNativeSelectionForCurrentInput()
    {
        _suppressNativeSelection = true;
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(() => _suppressNativeSelection = false);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        => e.Handled = HandleKeyboardShortcut(e.Key, e.KeyboardModifiers);

    internal bool HandleKeyboardShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.A && modifiers.HasFlag(ModifierKeys.Control))
        {
            _workspace.SelectAll();
            return true;
        }
        if (key == Key.Escape)
        {
            _workspace.DeselectAll();
            _selectionAnchor = null;
            return true;
        }
        return false;
    }

    internal bool HandleCardSelection(ModData? mod, bool isInteractive, ModifierKeys modifiers)
    {
        if (isInteractive)
            return false;
        if (mod is null)
        {
            _workspace.DeselectAll();
            _selectionAnchor = null;
            return true;
        }

        var control = modifiers.HasFlag(ModifierKeys.Control);
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            _workspace.SelectRange(_selectionAnchor ?? mod, mod, additive: control);
            _selectionAnchor = mod;
            return true;
        }
        if (control)
        {
            _workspace.SetSelected(mod, !_workspace.SelectedGuids.Contains(mod.Manifest.Guid));
            _selectionAnchor = mod;
            return true;
        }

        return false;
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e) => RefreshState();
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshTexts();

    private void RefreshState()
    {
        if (!ReferenceEquals(_list.ItemsSource, _workspace.Rows))
        {
            _updatingSelection = true;
            try { _list.ItemsSource = _workspace.Rows; }
            finally { _updatingSelection = false; }
        }
        _updatingSelection = true;
        try
        {
            foreach (var mod in _workspace.Rows.OfType<ModData>())
            {
                var selected = _workspace.SelectedGuids.Contains(mod.Manifest.Guid);
                if (selected && !_list.SelectedItems.Contains(mod))
                    _list.SelectedItems.Add(mod);
                else if (!selected && _list.SelectedItems.Contains(mod))
                    _list.SelectedItems.Remove(mod);
            }
        }
        finally { _updatingSelection = false; }
        _batchBar.Visibility = _workspace.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        _batchEnable.IsEnabled = _batchDisable.IsEnabled = _workspace.Mode == DashboardCatalogMode.Workspace;
        _selectionCount.Text = _localization["DashboardPage.SelectedCountPrefix"]
            + _workspace.SelectedCount + _localization["DashboardPage.SelectedCountSuffix"];
        _workspaceButton.IsEnabled = _workspace.Mode == DashboardCatalogMode.Library;
        _libraryButton.IsEnabled = _workspace.Mode == DashboardCatalogMode.Workspace;
        _selectedGroupName.Text = _workspace.Groups.SelectedGroup.Name;
        _groupButtons.Children.Clear();
        foreach (var group in _workspace.Groups.Groups)
        {
            var button = new Button { Content = group.Name, MinHeight = 36,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Foreground, Background = group.Id == _workspace.Groups.SelectedGroup.Id ? Accent : Brushes.Transparent,
                BorderBrush = Stroke, BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8) };
            button.Click += (_, _) => RunAsync(() => _workspace.SelectGroupAsync(group.Id));
            _groupButtons.Children.Add(button);
        }
        _removeGroupButton.IsEnabled = _workspace.SelectedCount > 0;
        _deleteGroupButton.IsEnabled = !_workspace.Groups.SelectedGroup.IsDefault;
    }

    private void RefreshTexts()
    {
        _searchHint.Text = _localization["JaliumMigration.SearchShort"];
        _search.ToolTip = _localization["DashboardPage.SearchWatermark"];
        _newGroupHint.Text = _localization["ModGroup.NewGroupWatermark"];
        _searchHint.Visibility = string.IsNullOrEmpty(_search.Text) ? Visibility.Visible : Visibility.Collapsed;
        _newGroupHint.Visibility = string.IsNullOrEmpty(_newGroupName.Text) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, key) in _localizedButtons)
        {
            if (button.Content is StackPanel stack && stack.Children.Count > 1
                && stack.Children[1] is TextBlock text)
                text.Text = _localization[key];
            else if (button.Content is TextBlock or Image)
                button.ToolTip = _localization[key];
            else
                button.Content = _localization[key];
        }
        _selectionCount.Text = _localization["DashboardPage.SelectedCountPrefix"]
            + _workspace.SelectedCount + _localization["DashboardPage.SelectedCountSuffix"];
        UpdateConflictBadges();
        SetVersionResults(_versionResults);
    }

    private void Dispatch(DashboardAction action, ModData? mod = null)
        => RunAsync(() => _actions.ExecuteAsync(action, mod));

    private async void RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { _reportError(ex); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SaveScrollPosition();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _workspace.Changed -= OnWorkspaceChanged;
        _localization.PropertyChanged -= OnLocalizationChanged;
        _list.PreviewMouseLeftButtonDown -= OnMouseDown;
        _list.PreviewMouseMove -= OnMouseMove;
        _list.DragOver -= OnDragOver;
        _list.Drop -= OnDrop;
    }

    private static Brush Brush(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));

    private static Brush ParseColor(string value)
    {
        try
        {
            var hex = value.TrimStart('#');
            var offset = hex.Length == 8 ? 2 : 0;
            return Brush(Convert.ToByte(hex.Substring(offset, 2), 16),
                Convert.ToByte(hex.Substring(offset + 2, 2), 16),
                Convert.ToByte(hex.Substring(offset + 4, 2), 16));
        }
        catch (Exception)
        {
            return Stroke;
        }
    }

}
