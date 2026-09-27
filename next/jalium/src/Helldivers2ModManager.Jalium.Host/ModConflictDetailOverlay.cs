using System.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ModConflictDetailOverlay : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Danger = Paint(0xDC, 0x50, 0x37);
    private static readonly Brush Success = Paint(0x28, 0xA0, 0x5F);
    private static readonly Brush Warning = Paint(0xDC, 0x9B, 0x2D);

    private readonly LocalizationService _localization;
    private readonly TextBlock _title = new();
    private readonly TextBlock _modName = new();
    private readonly TextBlock _statusIcon = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock[] _counts = [new(), new(), new()];
    private readonly TextBlock[] _countLabels = [new(), new(), new()];
    private readonly TextBlock _listTitle = new();
    private readonly TextBlock _emptyTitle = new();
    private readonly TextBlock _emptyDescription = new();
    private readonly Button _close = new();
    private readonly Border _statusCard;
    private readonly Border _emptyCard;
    private readonly StackPanel _items = new();
    private IReadOnlyList<ModConflictRecord> _conflicts = [];

    internal int DisplayedConflictCount => _items.Children.Count;
    internal string SummaryText => _summary.Text;

    public ModConflictDetailOverlay(LocalizationService localization)
    {
        _localization = localization;
        Visibility = Visibility.Collapsed;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        MouseLeftButtonDown += (_, _) => Close();
        KeyDown += OnKeyDown;

        var dialog = new Border { MaxWidth = 820, MaxHeight = 650, Margin = new Thickness(24),
            Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8) };
        dialog.MouseLeftButtonDown += (_, eventArgs) => eventArgs.Handled = true;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        _title.FontSize = 20;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        heading.Children.Add(_title);
        _modName.FontSize = 13;
        _modName.Foreground = Secondary;
        _modName.Margin = new Thickness(0, 4, 0, 0);
        _modName.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.Children.Add(_modName);
        header.Children.Add(heading);
        _close.Width = _close.Height = 36;
        _close.Margin = new Thickness(12, 0, 0, 0);
        _close.Content = new TextBlock { Text = "\uE8BB", FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14, Foreground = Foreground };
        _close.Click += (_, _) => Close();
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);
        layout.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var statusGrid = new Grid();
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _statusIcon.FontSize = 24;
        statusGrid.Children.Add(_statusIcon);
        var statusCopy = new StackPanel();
        _statusText.FontSize = 17;
        _statusText.FontWeight = FontWeights.SemiBold;
        statusCopy.Children.Add(_statusText);
        _summary.FontSize = 13;
        _summary.Foreground = Foreground;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 4, 0, 0);
        statusCopy.Children.Add(_summary);
        Grid.SetColumn(statusCopy, 1);
        statusGrid.Children.Add(statusCopy);
        _statusCard = Card(statusGrid);
        _statusCard.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(_statusCard);

        var stats = new Grid();
        for (var index = 0; index < 3; index++)
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++)
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _counts[index].FontSize = 20;
            _counts[index].FontWeight = FontWeights.SemiBold;
            _counts[index].Foreground = index == 1 ? Danger : Foreground;
            _counts[index].HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(_counts[index]);
            _countLabels[index].FontSize = 12;
            _countLabels[index].Foreground = Secondary;
            column.Children.Add(_countLabels[index]);
            Grid.SetColumn(column, index);
            stats.Children.Add(column);
        }
        var statsCard = Card(stats);
        statsCard.Margin = new Thickness(0, 0, 0, 16);
        body.Children.Add(statsCard);

        _listTitle.FontSize = 15;
        _listTitle.FontWeight = FontWeights.SemiBold;
        _listTitle.Foreground = Foreground;
        _listTitle.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(_listTitle);
        var empty = new StackPanel { Orientation = Orientation.Horizontal };
        empty.Children.Add(new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 18, Foreground = Success, Margin = new Thickness(0, 0, 12, 0) });
        var emptyCopy = new StackPanel();
        _emptyTitle.FontWeight = FontWeights.SemiBold;
        _emptyTitle.Foreground = Foreground;
        emptyCopy.Children.Add(_emptyTitle);
        _emptyDescription.FontSize = 12;
        _emptyDescription.Foreground = Secondary;
        _emptyDescription.TextWrapping = TextWrapping.Wrap;
        _emptyDescription.Margin = new Thickness(0, 3, 0, 0);
        emptyCopy.Children.Add(_emptyDescription);
        empty.Children.Add(emptyCopy);
        _emptyCard = Card(empty);
        _emptyCard.BorderBrush = Success;
        _emptyCard.Margin = new Thickness(0, 0, 0, 12);
        body.Children.Add(_emptyCard);
        body.Children.Add(_items);

        var scroll = new ScrollViewer { Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        dialog.Child = layout;
        Children.Add(dialog);
        _localization.PropertyChanged += OnLocalizationChanged;
        OnLocalizationChanged(this, new PropertyChangedEventArgs(null));
    }

    public void Show(string modName, IReadOnlyList<ModConflictRecord> conflicts)
    {
        _modName.Text = modName;
        _conflicts = conflicts;
        Render();
        Visibility = Visibility.Visible;
        Focus();
    }

    public void Close() => Visibility = Visibility.Collapsed;

    private void Render()
    {
        var visible = _conflicts
            .Where(static conflict => !string.IsNullOrWhiteSpace(conflict.FriendlyName))
            .OrderBy(static conflict => conflict.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasConflicts = visible.Length > 0;
        var statusBrush = hasConflicts ? Danger : Success;
        _statusCard.BorderBrush = statusBrush;
        _statusCard.Background = hasConflicts
            ? new SolidColorBrush(Color.FromArgb(0x18, 0xDC, 0x50, 0x37))
            : new SolidColorBrush(Color.FromArgb(0x18, 0x28, 0xA0, 0x5F));
        _statusIcon.Text = hasConflicts ? "!" : "\u2713";
        _statusIcon.Foreground = statusBrush;
        _statusText.Text = _localization[hasConflicts
            ? "ConflictDetail.ConflictFound" : "ConflictDetail.NoConflicts"];
        _statusText.Foreground = statusBrush;
        _summary.Text = _localization[hasConflicts
            ? "ConflictDetail.ConflictSummary" : "ConflictDetail.NoConflictsDescription"];
        _counts[0].Text = visible.Length.ToString();
        _counts[1].Text = _conflicts.Count(static conflict => conflict.IsDefiniteConflict).ToString();
        _counts[2].Text = _conflicts.SelectMany(static conflict => conflict.Participants)
            .Select(static participant => participant.ModGuid).Distinct().Count().ToString();
        _emptyCard.Visibility = hasConflicts ? Visibility.Collapsed : Visibility.Visible;
        _items.Children.Clear();
        foreach (var conflict in visible)
        {
            var definite = conflict.IsDefiniteConflict;
            var color = definite ? Danger : Warning;
            var participants = string.Join(", ", conflict.Participants
                .Select(static participant => participant.ModName)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new Border { Background = color });
            var icon = new TextBlock { Text = definite ? "!" : "?", FontSize = 17,
                Foreground = color, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0) };
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var copy = new StackPanel { Margin = new Thickness(8, 12, 14, 12) };
            copy.Children.Add(new TextBlock { Text = conflict.FriendlyName,
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Foreground, TextWrapping = TextWrapping.Wrap });
            copy.Children.Add(Detail(_localization["ConflictDetail.Unit"] + ": " + conflict.OriginalName, 2));
            copy.Children.Add(Detail(_localization["ConflictDetail.ConflictingMods"] + ": " + participants, 5));
            copy.Children.Add(Detail(_localization["ConflictDetail.CurrentWinner"] + ": "
                + conflict.Winner.ModName, 4, Foreground));
            Grid.SetColumn(copy, 2);
            row.Children.Add(copy);
            var card = Card(row);
            card.Padding = new Thickness(0);
            card.Margin = new Thickness(0, 0, 0, 8);
            _items.Children.Add(card);
        }
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        _title.Text = _localization["ConflictDetail.Title"];
        _close.ToolTip = _localization["MainWindow.Close"];
        _countLabels[0].Text = _localization["ConflictDetail.Conflicts"];
        _countLabels[1].Text = _localization["ConflictDetail.Definite"];
        _countLabels[2].Text = _localization["ArmorPollutionPage.AffectedMods"];
        _listTitle.Text = _localization["ConflictDetail.ResourceList"];
        _emptyTitle.Text = _localization["ConflictDetail.NoConflicts"];
        _emptyDescription.Text = _localization["ConflictDetail.NoConflictsDescription"];
        Render();
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
            return;
        Close();
        eventArgs.Handled = true;
    }

    public void Dispose()
    {
        _localization.PropertyChanged -= OnLocalizationChanged;
        KeyDown -= OnKeyDown;
    }

    private static Border Card(UIElement child) => new()
    {
        Child = child, Padding = new Thickness(16), Background = Surface,
        BorderBrush = Stroke, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
    };

    private static TextBlock Detail(string text, double top, Brush? foreground = null) => new()
    {
        Text = text, FontSize = 12, Foreground = foreground ?? Secondary,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };

    private static Brush Paint(byte red, byte green, byte blue)
        => new SolidColorBrush(Color.FromRgb(red, green, blue));
}
