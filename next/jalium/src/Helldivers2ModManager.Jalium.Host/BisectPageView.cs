using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class BisectPageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Surface = Paint(0x2E, 0x2E, 0x2E);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Accent = Paint(0x00, 0x78, 0xD4);
    private static readonly Brush Warning = Paint(0xE8, 0xB0, 0x58);

    private readonly DashboardWorkspace _workspace;
    private readonly BisectService _service;
    private readonly LocalizationService _localization;
    private readonly Action _back;
    private readonly Func<Task> _start;
    private readonly Func<Task> _resume;
    private readonly Func<Task> _abort;
    private readonly Action<Exception> _reportError;
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _hint = new();
    private readonly TextBlock _groupInfo = new();
    private readonly TextBlock _sessionInfo = new();
    private readonly TextBlock _suspects = new();
    private readonly Button _backButton = new();
    private readonly Button _startButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _abortButton = new();
    private readonly Border _roundsBorder;
    private readonly StackPanel _rounds = new();
    private bool _busy;

    public BisectPageView(DashboardWorkspace workspace, BisectService service,
        LocalizationService localization, Action back, Func<Task> start,
        Func<Task> resume, Func<Task> abort, Action<Exception> reportError)
    {
        _workspace = workspace;
        _service = service;
        _localization = localization;
        _back = back;
        _start = start;
        _resume = resume;
        _abort = abort;
        _reportError = reportError;
        Margin = new Thickness(20);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _backButton.Width = 38;
        _backButton.Height = 38;
        _backButton.Margin = new Thickness(0, 0, 12, 0);
        _backButton.Click += (_, _) => _back();
        header.Children.Add(_backButton);
        var headings = new StackPanel();
        _title.FontSize = 22;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Foreground;
        headings.Children.Add(_title);
        _description.FontSize = 12;
        _description.Foreground = Secondary;
        _description.TextWrapping = TextWrapping.Wrap;
        _description.Margin = new Thickness(0, 4, 0, 0);
        headings.Children.Add(_description);
        _hint.FontSize = 12;
        _hint.Foreground = Warning;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.Margin = new Thickness(0, 6, 0, 0);
        headings.Children.Add(_hint);
        Grid.SetColumn(headings, 1);
        header.Children.Add(headings);
        Children.Add(header);

        var session = new StackPanel();
        _groupInfo.Foreground = Foreground;
        _groupInfo.FontWeight = FontWeights.SemiBold;
        session.Children.Add(_groupInfo);
        _sessionInfo.Foreground = Secondary;
        _sessionInfo.FontSize = 12;
        _sessionInfo.Margin = new Thickness(0, 4, 0, 0);
        session.Children.Add(_sessionInfo);
        _suspects.Foreground = Accent;
        _suspects.FontSize = 12;
        _suspects.TextWrapping = TextWrapping.Wrap;
        _suspects.Margin = new Thickness(0, 6, 0, 0);
        session.Children.Add(_suspects);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0) };
        _startButton.MinWidth = _resumeButton.MinWidth = 130;
        _abortButton.MinWidth = 130;
        _startButton.Height = _resumeButton.Height = _abortButton.Height = 38;
        _abortButton.Margin = new Thickness(8, 0, 0, 0);
        _startButton.Click += (_, _) => RunAsync(_start);
        _resumeButton.Click += (_, _) => RunAsync(_resume);
        _abortButton.Click += (_, _) => RunAsync(_abort);
        buttons.Children.Add(_startButton);
        buttons.Children.Add(_resumeButton);
        buttons.Children.Add(_abortButton);
        session.Children.Add(buttons);
        var sessionBorder = new Border { Child = session, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 16) };
        Grid.SetRow(sessionBorder, 1);
        Children.Add(sessionBorder);

        var roundsLayout = new Grid();
        roundsLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        roundsLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var columns = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        AddColumns(columns);
        columns.Children.Add(ColumnHeading("Bisect.RoundColumn", 0));
        columns.Children.Add(ColumnHeading("Bisect.ModsColumn", 1));
        columns.Children.Add(ColumnHeading("Bisect.ResultColumn", 2));
        roundsLayout.Children.Add(columns);
        var scroll = new ScrollViewer { Content = _rounds,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        roundsLayout.Children.Add(scroll);
        _roundsBorder = new Border { Child = roundsLayout, Background = Surface,
            BorderBrush = Stroke, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(14) };
        Grid.SetRow(_roundsBorder, 2);
        Children.Add(_roundsBorder);

        _localization.PropertyChanged += OnLocalizationChanged;
        Refresh();
    }

    public void Refresh()
    {
        _title.Text = _localization["Bisect.Title"];
        _description.Text = _localization["Bisect.Description"];
        _hint.Text = _localization["Bisect.CrashHint"];
        _backButton.Content = new TextBlock { Text = "\u2190",
            FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = Foreground };
        _backButton.ToolTip = _localization["Common.Back"];
        _startButton.Content = _localization["Bisect.Start"];
        _resumeButton.Content = _localization["Bisect.Resume"];
        _abortButton.Content = _localization["Bisect.Abort"];
        var current = _service.Current;
        if (current is null)
        {
            var group = _workspace.Groups.SelectedGroup;
            var count = _workspace.Groups.FilterMods(_workspace.Mods).Count(mod => mod.Enabled);
            _groupInfo.Text = _localization["Bisect.GroupInfo"]
                .Replace("{name}", group.Name).Replace("{count}", count.ToString());
            _sessionInfo.Text = _localization["Bisect.SessionInactive"];
            _suspects.Text = string.Empty;
        }
        else
        {
            _groupInfo.Text = _localization["Bisect.OriginalGroupInfo"]
                .Replace("{name}", current.OriginalGroupName);
            _sessionInfo.Text = _localization["Bisect.CandidateCount"]
                .Replace("{count}", current.Candidates.Count.ToString());
            _suspects.Text = current.Suspects.Count == 0 ? string.Empty
                : _localization["Bisect.SuspectsLabel"] + "\n" + string.Join("\n",
                    current.Suspects.Select(guid => current.AllMods.FirstOrDefault(mod =>
                        mod.Manifest.Guid == guid)?.Manifest.Name ?? guid.ToString()));
        }
        _startButton.Visibility = current is null ? Visibility.Visible : Visibility.Collapsed;
        _resumeButton.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        _abortButton.Visibility = _resumeButton.Visibility;
        _startButton.IsEnabled = _resumeButton.IsEnabled = _abortButton.IsEnabled = !_busy;
        _backButton.IsEnabled = !_busy && current is null;
        _roundsBorder.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        _rounds.Children.Clear();
        if (current is not null)
            foreach (var round in current.Rounds)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                AddColumns(row);
                row.Children.Add(Cell(round.RoundIndex.ToString(), 0));
                row.Children.Add(Cell(string.Join(", ", round.TestedModNames), 1));
                row.Children.Add(Cell(_localization[round.Crashed
                    ? "Bisect.RoundCrashed" : "Bisect.RoundOk"], 2));
                _rounds.Children.Add(row);
            }
    }

    private async void RunAsync(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        Refresh();
        try { await action(); }
        catch (Exception ex) { _reportError(ex); }
        finally { _busy = false; Refresh(); }
    }

    private TextBlock ColumnHeading(string key, int column)
    {
        var heading = Cell(_localization[key], column);
        heading.FontWeight = FontWeights.SemiBold;
        return heading;
    }

    private static TextBlock Cell(string text, int column)
    {
        var cell = new TextBlock { Text = text, Foreground = column == 1 ? Foreground : Secondary,
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cell, column);
        return cell;
    }

    private static void AddColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
    }

    private void OnLocalizationChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private static SolidColorBrush Paint(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    public void Dispose() => _localization.PropertyChanged -= OnLocalizationChanged;
}
