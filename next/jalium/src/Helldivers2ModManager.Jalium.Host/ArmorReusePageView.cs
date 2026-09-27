using System.ComponentModel;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed class ArmorReusePageView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xF2, 0xF2, 0xF2);
    private static readonly Brush Secondary = Paint(0xB3, 0xB3, 0xB3);
    private static readonly Brush Stroke = Paint(0x46, 0x46, 0x46);
    private static readonly Brush Accent = Paint(0x00, 0x78, 0xD4);
    private readonly LocalizationService _localization;
    private readonly BackgroundTaskService _tasks;
    private readonly Func<IReadOnlyList<ModData>, CancellationToken, Task<ArmorReuseAnalysisResult>> _scan;
    private readonly Func<IReadOnlyList<ModData>> _enabledMods;
    private readonly Action _back;
    private readonly TextBlock _title = new();
    private readonly TextBlock _description = new();
    private readonly TextBlock _attribution = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock[] _counts = [new(), new(), new(), new()];
    private readonly TextBlock[] _countLabels = [new(), new(), new(), new()];
    private readonly Border _empty = new();
    private readonly TextBlock _emptyTitle = new();
    private readonly TextBlock _emptyDescription = new();
    private readonly StackPanel _results = new();
    private readonly ScrollViewer _resultScroll;
    private readonly Button _backButton = new();
    private readonly Button _refreshButton = new();
    private ArmorReuseAnalysisResult? _result;
    private CancellationTokenSource? _scanCancellation;
    private bool _disposed;

    public ArmorReusePageView(LocalizationService localization, BackgroundTaskService tasks,
        Func<IReadOnlyList<ModData>> enabledMods,
        Func<IReadOnlyList<ModData>, CancellationToken, Task<ArmorReuseAnalysisResult>> scan,
        Action back)
    {
        _localization = localization;
        _tasks = tasks;
        _enabledMods = enabledMods;
        _scan = scan;
        _back = back;
        Margin = new Thickness(20);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _backButton.Width = _backButton.Height = 38;
        _backButton.Margin = new Thickness(0, 0, 12, 0);
        _backButton.Content = Icon("\uE72B");
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
        _attribution.FontSize = 11;
        _attribution.Foreground = Secondary;
        _attribution.TextWrapping = TextWrapping.Wrap;
        _attribution.Margin = new Thickness(0, 5, 0, 0);
        headings.Children.Add(_attribution);
        Grid.SetColumn(headings, 1);
        header.Children.Add(headings);

        var refreshContent = new StackPanel { Orientation = Orientation.Horizontal };
        refreshContent.Children.Add(Icon("\uE72C"));
        refreshContent.Children.Add(new TextBlock { Name = "RefreshLabel", Margin = new Thickness(7, 0, 0, 0) });
        _refreshButton.Content = refreshContent;
        _refreshButton.Padding = new Thickness(14, 8, 14, 8);
        _refreshButton.Click += async (_, _) => await ScanAsync();
        Grid.SetColumn(_refreshButton, 2);
        header.Children.Add(_refreshButton);
        Children.Add(header);

        var stats = new Grid();
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < 4; index++)
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _summary.Foreground = Secondary;
        _summary.TextWrapping = TextWrapping.Wrap;
        _summary.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetColumnSpan(_summary, 4);
        stats.Children.Add(_summary);
        for (var index = 0; index < 4; index++)
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _counts[index].FontSize = 20;
            _counts[index].FontWeight = FontWeights.SemiBold;
            _counts[index].Foreground = index == 3 ? Accent : Foreground;
            _counts[index].HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(_counts[index]);
            _countLabels[index].FontSize = 12;
            _countLabels[index].Foreground = Secondary;
            column.Children.Add(_countLabels[index]);
            Grid.SetRow(column, 1);
            Grid.SetColumn(column, index);
            stats.Children.Add(column);
        }
        var statsBorder = Card(stats);
        statsBorder.Margin = new Thickness(0, 0, 0, 16);
        Grid.SetRow(statsBorder, 1);
        Children.Add(statsBorder);

        var resultArea = new Grid();
        var emptyText = new StackPanel();
        _emptyTitle.FontWeight = FontWeights.SemiBold;
        _emptyTitle.Foreground = Foreground;
        emptyText.Children.Add(_emptyTitle);
        _emptyDescription.FontSize = 12;
        _emptyDescription.Foreground = Secondary;
        _emptyDescription.TextWrapping = TextWrapping.Wrap;
        _emptyDescription.Margin = new Thickness(0, 4, 0, 0);
        emptyText.Children.Add(_emptyDescription);
        _empty.Child = emptyText;
        _empty.Padding = new Thickness(18);
        _empty.CornerRadius = new CornerRadius(6);
        _empty.BorderBrush = Paint(0x10, 0x7C, 0x10);
        _empty.BorderThickness = new Thickness(1);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        resultArea.Children.Add(_empty);
        _resultScroll = new ScrollViewer { Content = _results,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        resultArea.Children.Add(_resultScroll);
        Grid.SetRow(resultArea, 2);
        Children.Add(resultArea);

        _localization.PropertyChanged += OnLocalizationChanged;
        RefreshTexts();
        RenderResult();
    }

    public async Task ScanAsync()
    {
        if (_disposed || _scanCancellation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        _refreshButton.IsEnabled = false;
        _summary.Text = _localization["ArmorReusePage.Scanning"];
        try
        {
            var mods = _enabledMods();
            var result = await _tasks.RunAsync(
                _localization["BackgroundTasksPage.TaskTypeArmorReuseScan"], _summary.Text,
                (_, token) => _scan(mods, token), _summary.Text, cancellation.Token);
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                _result = result;
                RenderResult();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed)
                _summary.Text = _localization["ArmorReusePage.ScanFailed"];
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, cancellation))
                _scanCancellation = null;
            if (!_disposed)
                _refreshButton.IsEnabled = true;
        }
    }

    private void RenderResult()
    {
        var records = _result?.Records ?? [];
        var reusedCount = records.Sum(static record => record.ReusedBy.Count);
        _counts[0].Text = (_result?.ScannedModCount ?? 0).ToString();
        _counts[1].Text = (_result?.ScannedPatchCount ?? 0).ToString();
        _counts[2].Text = (_result?.AffectedModCount ?? 0).ToString();
        _counts[3].Text = reusedCount.ToString();
        if (_result is not null)
            _summary.Text = records.Count == 0 ? _localization["ArmorReusePage.None"]
                : _localization["ArmorReusePage.Found"]
                    .Replace("{records}", records.Count.ToString())
                    .Replace("{armors}", reusedCount.ToString());
        _empty.Visibility = records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _resultScroll.Visibility = records.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _results.Children.Clear();
        foreach (var record in records)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = record.ModName, FontSize = 14,
                FontWeight = FontWeights.SemiBold, Foreground = Foreground });
            content.Children.Add(new TextBlock { Text = _localization["ArmorReusePage.SourceArmor"]
                + record.SourceArmorName + " (0x" + record.SourceArmorId.ToUpperInvariant() + ")",
                FontSize = 12, Foreground = Foreground, Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = string.Join("、",
                record.ReusedBy.Select(static target => target.ArmorName)),
                Foreground = Accent, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0) });
            content.Children.Add(new TextBlock { Text = _localization["ArmorReusePage.SharedUnits"]
                    .Replace("{count}", record.SharedUnitCount.ToString()),
                FontSize = 11, Foreground = Secondary, Margin = new Thickness(0, 4, 0, 0) });
            var card = Card(content);
            card.Margin = new Thickness(0, 0, 0, 8);
            _results.Children.Add(card);
        }
    }

    private void RefreshTexts()
    {
        _title.Text = _localization["DashboardPage.ArmorReuse"];
        _description.Text = _localization["ArmorReusePage.Description"];
        _attribution.Text = _localization["ArmorReusePage.Attribution"];
        _backButton.ToolTip = _localization["Common.Back"];
        ((TextBlock)((StackPanel)_refreshButton.Content!).Children[1]).Text =
            _localization["ArmorPollutionPage.Refresh"];
        _countLabels[0].Text = _localization["ArmorPollutionPage.ScannedMods"];
        _countLabels[1].Text = _localization["ArmorPollutionPage.ScannedPatches"];
        _countLabels[2].Text = _localization["ArmorPollutionPage.AffectedMods"];
        _countLabels[3].Text = _localization["ArmorReusePage.ReusedArmors"];
        _emptyTitle.Text = _localization["ArmorReusePage.EmptyTitle"];
        _emptyDescription.Text = _localization["ArmorReusePage.EmptyDescription"];
        if (_scanCancellation is null)
            RenderResult();
    }

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) => RefreshTexts();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _localization.PropertyChanged -= OnLocalizationChanged;
        _scanCancellation?.Cancel();
    }

    private static Border Card(UIElement content) => new()
    {
        Child = content, Padding = new Thickness(16), CornerRadius = new CornerRadius(6),
        Background = Paint(0x2E, 0x2E, 0x2E), BorderBrush = Stroke,
        BorderThickness = new Thickness(1),
    };

    private static TextBlock Icon(string glyph) => new()
    {
        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"),
        FontSize = 16, Foreground = Foreground,
    };

    private static Brush Paint(byte red, byte green, byte blue)
        => new SolidColorBrush(Color.FromRgb(red, green, blue));
}
