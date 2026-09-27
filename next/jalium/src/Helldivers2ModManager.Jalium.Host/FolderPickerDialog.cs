using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class FolderPickerDialog : Window
{
    private readonly LocalizationService _localization;
    private readonly TextBox _path = new();
    private readonly StackPanel _directories = new();
    private readonly TextBlock _status = new();
    private string _currentPath;

    public bool Accepted { get; private set; }
    public string? SelectedPath { get; private set; }

    public FolderPickerDialog(string title, string? initialPath,
        LocalizationService localization, Window owner)
    {
        _localization = localization;
        _currentPath = ResolveInitialPath(initialPath);
        Title = title;
        Width = 560;
        Height = 520;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var pathRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _path.Height = 36;
        _path.IsReadOnly = true;
        pathRow.Children.Add(_path);
        var up = new Button { Content = _localization["Common.Back"], Height = 36,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0),
            ToolTip = _localization["Common.Back"] };
        up.Click += (_, _) => NavigateToParent();
        Grid.SetColumn(up, 1);
        pathRow.Children.Add(up);
        root.Children.Add(pathRow);

        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
        _status.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        var listBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { Content = _directories,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        Grid.SetRow(listBorder, 2);
        root.Children.Add(listBorder);

        var commands = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var create = new Button { Content = _localization["FolderPicker.NewFolder"], Height = 36,
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        create.Click += (_, _) => CreateFolder();
        commands.Children.Add(create);
        var select = new Button { Content = _localization["Common.OK"], Width = 92, Height = 36 };
        select.Click += (_, _) =>
        {
            Accepted = true;
            SelectedPath = _currentPath;
            Close();
        };
        commands.Children.Add(select);
        var cancel = new Button { Content = _localization["Common.Cancel"], Width = 92,
            Height = 36, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        commands.Children.Add(cancel);
        Grid.SetRow(commands, 3);
        root.Children.Add(commands);
        Content = root;
        RefreshDirectories();
    }

    private static string ResolveInitialPath(string? initialPath)
    {
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            return Path.GetFullPath(initialPath);
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private void RefreshDirectories()
    {
        _path.Text = _currentPath;
        _directories.Children.Clear();
        try
        {
            var directories = new DirectoryInfo(_currentPath).EnumerateDirectories()
                .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _status.Text = directories.Length == 0
                ? _localization["FolderPicker.Empty"]
                : _localization["FolderPicker.SelectHint"];
            foreach (var directory in directories)
            {
                var entry = new Button { Content = directory.Name, Height = 34,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 2, 4, 2),
                    ToolTip = directory.FullName };
                entry.Click += (_, _) =>
                {
                    _currentPath = directory.FullName;
                    RefreshDirectories();
                };
                _directories.Children.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or DirectoryNotFoundException or PathTooLongException)
        {
            _status.Text = ex.Message;
        }
    }

    private void NavigateToParent()
    {
        var parent = Directory.GetParent(_currentPath);
        if (parent is null) return;
        _currentPath = parent.FullName;
        RefreshDirectories();
    }

    private void CreateFolder()
    {
        var dialog = new Window { Title = _localization["FolderPicker.NewFolderTitle"],
            Width = 420, Height = 190, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = _localization["FolderPicker.NewFolderPrompt"],
            TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { Height = 36, Margin = new Thickness(0, 10, 0, 12) };
        panel.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = _localization["Common.Cancel"], Width = 88, Height = 36 };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);
        var confirm = new Button { Content = _localization["Common.Confirm"], Width = 88,
            Height = 36, Margin = new Thickness(8, 0, 0, 0) };
        confirm.Click += (_, _) =>
        {
            var value = name.Text.Trim();
            if (value.Length == 0 || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return;
            try
            {
                Directory.CreateDirectory(Path.Combine(_currentPath, value));
                dialog.Close();
                RefreshDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or PathTooLongException)
            {
                _status.Text = ex.Message;
            }
        };
        buttons.Children.Add(confirm);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }
}
