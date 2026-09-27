using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class IncludeDirectoryPickerView : Window
{
    private readonly List<(string Path, CheckBox Check)> _choices = [];

    public bool Accepted { get; private set; }
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    public IncludeDirectoryPickerView(string sourceDirectory, string existingPaths,
        LocalizationService localization, Window owner)
    {
        Title = localization["CreateWizard.IncludePickerTitle"];
        Width = 480;
        Height = 520;
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var selected = CreateOptionDraft.ParsePaths(existingPaths)
            .Select(path => path.Replace('/', '\\'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock { Text = localization["CreateWizard.IncludePickerDesc"],
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });

        var tree = new StackPanel { Margin = new Thickness(8) };
        tree.Children.Add(new TextBlock { Text = new DirectoryInfo(sourceDirectory).Name,
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8) });
        AddDirectories(tree, new DirectoryInfo(sourceDirectory), string.Empty, 0, selected);
        var scroll = new ScrollViewer { Content = tree,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var frame = new Border { Child = scroll,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(frame, 1);
        layout.Children.Add(frame);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = localization["Common.OK"], Width = 80,
            Height = 32, Margin = new Thickness(0, 0, 8, 0) };
        accept.Click += (_, _) =>
        {
            SelectedPaths = _choices.Where(choice => choice.Check.IsChecked == true)
                .Select(choice => choice.Path).ToArray();
            Accepted = true;
            Close();
        };
        buttons.Children.Add(accept);
        var cancel = new Button { Content = localization["Common.Cancel"], Width = 80, Height = 32 };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = layout;
    }

    private void AddDirectories(StackPanel tree, DirectoryInfo directory, string relative,
        int depth, HashSet<string> selected)
    {
        try
        {
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var path = relative.Length == 0 ? child.Name : relative + "\\" + child.Name;
                var check = new CheckBox { Content = child.Name, IsChecked = selected.Contains(path),
                    MinHeight = 28, Margin = new Thickness(16 + depth * 16, 0, 0, 0) };
                _choices.Add((path, check));
                tree.Children.Add(check);
                AddDirectories(tree, child, path, depth + 1, selected);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or PathTooLongException) { }
    }
}
