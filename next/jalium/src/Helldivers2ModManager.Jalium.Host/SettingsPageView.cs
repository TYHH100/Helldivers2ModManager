using System.ComponentModel;
using System.Security.Principal;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class SettingsPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xFF, 0xFF, 0xFF);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x3A, 0x3A, 0x3A);
    private readonly SettingsEditor _editor;
    private readonly LocalizationService _localization;
    private readonly Window _owner;
    private readonly MessageBoxOverlay _messageBoxOverlay;
    private readonly Func<Task> _save;
    private readonly Func<Task> _cancel;
    private readonly Action _openAutoTagPairing;
    private readonly Func<Task> _replayTutorial;
    private readonly Func<Task> _recomputeHashes;
    private readonly Func<Task> _hardPurge;
    private readonly Func<Task> _resetSettings;
    private readonly Action<bool>? _setMusicEnabled;
    private readonly Action<Exception> _reportError;
    private readonly TextBlock _title = new();
    private readonly StackPanel _tabButtons = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button _cancelButton = new();
    private readonly Button _saveButton = new();
    private readonly TextBlock _status = new();
    private int _selectedTab;
    private bool _busy;

    public SettingsPageView(SettingsEditor editor, LocalizationService localization, Window owner,
        Func<Task> save, Func<Task> cancel, Action openAutoTagPairing,
        Func<Task> replayTutorial, Func<Task> recomputeHashes,
        Func<Task> hardPurge, Func<Task> resetSettings, MessageBoxOverlay messageBoxOverlay,
        Action<Exception> reportError,
        Action<bool>? setMusicEnabled = null)
    {
        _editor = editor;
        _localization = localization;
        _owner = owner;
        _messageBoxOverlay = messageBoxOverlay;
        _save = save;
        _cancel = cancel;
        _openAutoTagPairing = openAutoTagPairing;
        _replayTutorial = replayTutorial;
        _recomputeHashes = recomputeHashes;
        _hardPurge = hardPurge;
        _resetSettings = resetSettings;
        _setMusicEnabled = setMusicEnabled;
        _reportError = reportError;
        Margin = new Thickness(16);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _title.FontSize = 28;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        _title.Margin = new Thickness(0, 0, 0, 16);
        Children.Add(_title);

        var tabs = new Border { Child = _tabButtons, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(6) };
        Grid.SetRow(tabs, 1);
        Children.Add(tabs);

        var body = new Border { Child = _scroll, BorderBrush = Stroke,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(body, 2);
        Children.Add(body);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Foreground = Secondary;
        _status.FontSize = 12;
        _status.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        _cancelButton.MinWidth = 96;
        _cancelButton.Height = 38;
        _cancelButton.Margin = new Thickness(0, 0, 8, 0);
        _cancelButton.Click += (_, _) => RunAsync(_cancel);
        actions.Children.Add(_cancelButton);
        _saveButton.MinWidth = 96;
        _saveButton.Height = 38;
        _saveButton.Click += (_, _) => RunAsync(_save);
        actions.Children.Add(_saveButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        Children.Add(new Border { Child = footer, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8) }
            .WithRow(3));

        _localization.PropertyChanged += OnLocalizationChanged;
        Refresh();
    }

    internal void Refresh()
    {
        _title.Text = _localization["DashboardPage.Settings"];
        _cancelButton.Content = _localization["Common.Cancel"];
        _saveButton.Content = _localization["Common.OK"];
        _tabButtons.Children.Clear();
        var labels = new[] { "SettingsPage.TabPaths", "SettingsPage.TabDeploy",
            "ModelPreviewPage.Mods", "SettingsPage.TabLogs", "SettingsPage.Tools", "SettingsPage.TabHome",
            "SettingsPage.TabAppearance" };
        var icons = new[] { "\uE838", "\uE7B8", "\uE8F1", "\uE9A4", "\uE713", "\uE80F", "\uE790" };
        for (var index = 0; index < labels.Length; index++)
        {
            var tabIndex = index;
            var button = new Button { Content = IconLabel(icons[index], _localization[labels[index]]),
                Height = 36, Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 4, 0),
                Background = index == _selectedTab ? Paint(0x45, 0x45, 0x45) : Surface };
            button.Click += (_, _) => SelectTab(tabIndex);
            _tabButtons.Children.Add(button);
        }
        _scroll.Content = _selectedTab switch
        {
            0 => BuildPaths(),
            1 => BuildDeployment(),
            2 => BuildMods(),
            3 => BuildLogs(),
            4 => BuildTools(),
            5 => BuildHome(),
            _ => BuildAppearance(),
        };
    }

    internal void SelectTab(int index)
    {
        _selectedTab = Math.Clamp(index, 0, 6);
        Refresh();
    }

    private StackPanel BuildPaths()
    {
        var page = new StackPanel();
        page.Children.Add(PathCard("SettingsPage.GameDir", "SettingsPage.GameDirDesc",
            _editor.Settings.GameDirectory, "SettingsPage.BrowseGameDialog", _editor.SetGameDirectory,
            includeDetect: true));
        page.Children.Add(PathCard("SettingsPage.StorageDir", "SettingsPage.StorageDirDesc",
            _editor.Settings.StorageDirectory, "SettingsPage.BrowseStorageDialog",
            _editor.SetStorageDirectory, "SettingsPage.StorageDirWarning"));
        page.Children.Add(PathCard("SettingsPage.TempDir", "SettingsPage.TempDirDesc1",
            _editor.Settings.TempDirectory, "SettingsPage.BrowseTempDialog",
            _editor.SetTempDirectory));

        var folders = Card("SettingsPage.OrgFolders", "SettingsPage.OrgFoldersDesc");
        var list = new ListBox { ItemsSource = _editor.Settings.OrganizationalFolderNames,
            Width = 400, MinHeight = 80, MaxHeight = 150,
            HorizontalAlignment = HorizontalAlignment.Left };
        folders.Children.Add(list);
        var commands = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0) };
        var add = CommandButton("\uE710", "SettingsPage.Add");
        add.Click += (_, _) => RunAsync(async () =>
        {
            var name = await PromptFolderNameAsync();
            if (name is null) return;
            try
            {
                if (!_editor.AddOrganizationFolder(name))
                    _status.Text = _localization["SettingsPage.AddOrgFolderExists"].Replace("{name}", name.Trim());
            }
            catch (Exception ex) { _reportError(ex); }
        });
        commands.Children.Add(add);
        var remove = CommandButton("\uE74D", "Common.Delete");
        remove.Margin = new Thickness(8, 0, 0, 0);
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is string name)
                _editor.RemoveOrganizationFolder(name);
        };
        commands.Children.Add(remove);
        folders.Children.Add(commands);
        page.Children.Add(WrapCard(folders));
        return page;
    }

    private Border PathCard(string titleKey, string descriptionKey, string currentPath,
        string dialogTitleKey, Action<string> setPath, string? warningKey = null, bool includeDetect = false)
    {
        var content = Card(titleKey, descriptionKey);
        if (warningKey is not null)
            content.Children.Add(new TextBlock { Text = _localization[warningKey],
                Foreground = Paint(0xFF, 0x80, 0x80), FontSize = 12,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (includeDetect) line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var path = new TextBox { Text = currentPath, IsReadOnly = true, Height = 36,
            VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(path);
        if (includeDetect)
        {
            var detect = new Button { Content = _localization["SettingsPage.AutoDetect"],
                Height = 36, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
            detect.Click += (_, _) => RunAsync(async () =>
            {
                detect.IsEnabled = false;
                try
                {
                    var found = await _editor.DetectGameAsync();
                    if (found is null) _status.Text = _localization["SettingsPage.DetectGameFailed"];
                    else { setPath(found); path.Text = found; }
                }
                finally { detect.IsEnabled = true; }
            });
            Grid.SetColumn(detect, 1);
            line.Children.Add(detect);
        }
        var browse = new Button { Content = _localization["CreatePage.OptionBrowse"],
            Height = 36, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            try
            {
                var dialog = new FolderPickerDialog(_localization[dialogTitleKey],
                    path.Text, _localization, _owner);
                dialog.ShowDialog();
                if (dialog.Accepted && dialog.SelectedPath is not null)
                {
                    setPath(dialog.SelectedPath);
                    path.Text = includeDetect ? _editor.Settings.GameDirectory : dialog.SelectedPath;
                }
            }
            catch (Exception ex) { _reportError(ex); }
        };
        Grid.SetColumn(browse, includeDetect ? 2 : 1);
        line.Children.Add(browse);
        content.Children.Add(line);
        return WrapCard(content);
    }

    private StackPanel BuildDeployment()
    {
        var page = new StackPanel();
        var method = Card("SettingsPage.DeployMethod", "SettingsPage.SymLinkDesc1");
        method.Children.Add(Description("SettingsPage.SymLinkDesc2"));
        method.Children.Add(Description("SettingsPage.HardLinkDesc"));
        method.Children.Add(Toggle("SettingsPage.UseSymLinks", () => _editor.Settings.UseSymbolicLinks,
            value =>
            {
                _editor.SetSymbolicLinks(value);
                if (value && !IsAdministrator())
                    _status.Text = _localization["SettingsPage.SymbolicLinkAdminMsg"];
                Refresh();
            }));
        method.Children.Add(Toggle("SettingsPage.UseHardLinks", () => _editor.Settings.UseHardLinks,
            value => { _editor.SetHardLinks(value); Refresh(); }));
        method.Children.Add(Heading("SettingsPage.DeployOrder", new Thickness(0, 12, 0, 4)));
        method.Children.Add(Description("SettingsPage.DeployOrderDesc"));
        var top = new RadioButton { Content = _localization["SettingsPage.DeployTopToBottom"],
            IsChecked = !_editor.Settings.DeployBottomToTop, GroupName = "DeployOrder",
            Margin = new Thickness(0, 8, 0, 4) };
        top.Click += (_, _) => _editor.Settings.DeployBottomToTop = false;
        method.Children.Add(top);
        var bottom = new RadioButton { Content = _localization["SettingsPage.DeployBottomToTop"],
            IsChecked = _editor.Settings.DeployBottomToTop, GroupName = "DeployOrder" };
        bottom.Click += (_, _) => _editor.Settings.DeployBottomToTop = true;
        method.Children.Add(bottom);
        page.Children.Add(WrapCard(method));

        var order = Card("SettingsPage.CustomDeployOrder", "SettingsPage.CustomDeployOrderDesc");
        order.Children.Add(Toggle("SettingsPage.CustomDeployOrder", () => _editor.Settings.UseDeploymentOrder,
            value => { _editor.Settings.UseDeploymentOrder = value; Refresh(); }));
        if (_editor.Settings.UseDeploymentOrder)
            order.Children.Add(Description("SettingsPage.CustomDeployOrderHint"));
        page.Children.Add(WrapCard(order));
        return page;
    }

    private CheckBox Toggle(string key, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox { Content = _localization[key], IsChecked = get(),
            MinHeight = 30, Margin = new Thickness(0, 8, 0, 0) };
        check.Click += (_, _) =>
        {
            try { set(check.IsChecked == true); }
            catch (Exception ex) { check.IsChecked = get(); _reportError(ex); }
        };
        return check;
    }

    private StackPanel Card(string titleKey, string descriptionKey)
    {
        var content = new StackPanel();
        content.Children.Add(Heading(titleKey));
        content.Children.Add(Description(descriptionKey));
        return content;
    }

    private TextBlock Heading(string key, Thickness? margin = null) => new()
    {
        Text = _localization[key], FontSize = 16, FontWeight = FontWeights.SemiBold,
        Foreground = Foreground, Margin = margin ?? new Thickness(0),
    };

    private TextBlock Description(string key) => new()
    {
        Text = _localization[key], FontSize = 13, Foreground = Secondary,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12),
    };

    private static Border WrapCard(StackPanel content) => new()
    {
        Child = content, Background = Surface, BorderBrush = Stroke,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12),
    };

    private Button CommandButton(string glyph, string labelKey) => new()
    {
        Content = IconLabel(glyph, _localization[labelKey]),
        Height = 36, Padding = new Thickness(12, 6, 12, 6),
    };

    private static StackPanel IconLabel(string glyph, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private Task<string?> PromptFolderNameAsync() =>
        _messageBoxOverlay.PromptAsync(_localization["SettingsPage.AddOrgFolderTitle"],
            _localization["SettingsPage.AddOrgFolderMsg"], string.Empty, 100,
            value => string.IsNullOrWhiteSpace(value) ? _localization["SettingsPage.AddOrgFolderMsg"] : null);

    private async void RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        _saveButton.IsEnabled = false;
        _cancelButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { _reportError(ex); }
        finally
        {
            _busy = false;
            _saveButton.IsEnabled = true;
            _cancelButton.IsEnabled = true;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;
    private static Brush Paint(byte red, byte green, byte blue)
        => new SolidColorBrush(Color.FromRgb(red, green, blue));
}

internal static class SettingsPageGridExtensions
{
    internal static T WithRow<T>(this T element, int row) where T : UIElement
    {
        Grid.SetRow(element, row);
        return element;
    }
}
