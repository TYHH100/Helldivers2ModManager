using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class PatchResourceViewerPageView
{
    private readonly ListBox _audioEntries = new();
    private readonly TextBox _audioFilter = new();
    private readonly CheckBox _audioModifiedOnly = new();
    private readonly TextBlock _audioCount = new();
    private readonly TextBlock _audioModifiedCount = new();
    private readonly TextBlock _audioMessage = new();
    private readonly TextBlock _audioTime = new();
    private readonly Button _audioPlayButton = new();
    private readonly Button _audioStopButton = new();
    private readonly Slider _audioPosition = new();
    private readonly Slider _audioVolume = new();
    private AudioPlaybackService _audioPlayer = null!;
    private Dispatcher _audioDispatcher = null!;
    private System.Threading.Timer _audioTimer = null!;
    private CancellationTokenSource? _audioPlaybackCancellation;
    private AudioRow[] _audioRows = [];
    private AudioInventoryResult _audioInventory = AudioInventoryResult.Empty;
    private AudioEntry? _currentAudioEntry;
    private bool _updatingAudioPosition;
    private int _audioPlaybackGeneration;

    private void InitializeAudio()
    {
        _audioPlayer = new AudioPlaybackService(NullLogger<AudioPlaybackService>.Instance);
        _audioDispatcher = Dispatcher.CurrentDispatcher;
        _audioPlayer.PlaybackEnded += OnAudioPlaybackEnded;
        _audioTimer = new System.Threading.Timer(_ => _audioDispatcher.BeginInvoke(UpdateAudioPosition),
            null, Timeout.Infinite, Timeout.Infinite);
    }

    private Grid BuildAudioTab()
    {
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _audioPlayButton.Width = _audioPlayButton.Height = 30;
        _audioPlayButton.Margin = new Thickness(0, 0, 4, 0);
        _audioPlayButton.Click += (_, _) => _ = ToggleAudioAsync();
        controls.Children.Add(_audioPlayButton);
        _audioStopButton.Width = _audioStopButton.Height = 30;
        _audioStopButton.Margin = new Thickness(0, 0, 8, 0);
        _audioStopButton.Click += (_, _) => StopAudio();
        Grid.SetColumn(_audioStopButton, 1);
        controls.Children.Add(_audioStopButton);
        _audioPosition.Minimum = 0;
        _audioPosition.Maximum = 1;
        _audioPosition.Margin = new Thickness(0, 0, 8, 0);
        _audioPosition.ValueChanged += (_, _) =>
        {
            if (!_updatingAudioPosition && _currentAudioEntry is not null)
                _audioPlayer.Seek(_audioPosition.Value);
        };
        Grid.SetColumn(_audioPosition, 2);
        controls.Children.Add(_audioPosition);
        _audioTime.Text = "00:00 / 00:00";
        _audioTime.FontSize = 11;
        _audioTime.Foreground = Secondary;
        _audioTime.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_audioTime, 3);
        controls.Children.Add(_audioTime);
        _audioVolume.Minimum = 0;
        _audioVolume.Maximum = 100;
        _audioVolume.Value = 100;
        _audioVolume.Margin = new Thickness(0, 0, 8, 0);
        _audioVolume.ValueChanged += (_, _) => _audioPlayer.SetVolume((float)(_audioVolume.Value / 100));
        Grid.SetColumn(_audioVolume, 4);
        controls.Children.Add(_audioVolume);
        _audioFilter.Height = 30;
        _audioFilter.Width = 250;
        _audioFilter.HorizontalAlignment = HorizontalAlignment.Left;
        _audioFilter.Margin = new Thickness(0, 5, 0, 0);
        _audioFilter.TextChanged += (_, _) => ApplyAudioFilter();
        Grid.SetRow(_audioFilter, 1);
        Grid.SetColumnSpan(_audioFilter, 6);
        controls.Children.Add(_audioFilter);
        root.Children.Add(controls);

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _audioCount.FontSize = 11;
        _audioCount.Foreground = Secondary;
        summary.Children.Add(_audioCount);
        _audioMessage.FontSize = 11;
        _audioMessage.Foreground = Foreground;
        _audioMessage.Margin = new Thickness(10, 0, 8, 0);
        _audioMessage.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(_audioMessage, 1);
        summary.Children.Add(_audioMessage);
        var comparison = new StackPanel { Orientation = Orientation.Horizontal };
        _audioModifiedCount.FontSize = 11;
        _audioModifiedCount.Foreground = Secondary;
        _audioModifiedCount.Margin = new Thickness(0, 0, 8, 0);
        comparison.Children.Add(_audioModifiedCount);
        _audioModifiedOnly.Click += (_, _) => ApplyAudioFilter();
        comparison.Children.Add(_audioModifiedOnly);
        Grid.SetColumn(comparison, 2);
        summary.Children.Add(comparison);
        Grid.SetRow(summary, 1);
        root.Children.Add(summary);

        ConfigureGroupedList(_audioEntries);
        _audioEntries.ItemTemplate = CreateAudioEntryTemplate();
        _audioEntries.SelectionChanged += (_, _) =>
        {
            if (_audioEntries.SelectedItem is GroupedListRow<AudioRow> { Entry: null })
                _audioEntries.SelectedItem = null;
        };
        Grid.SetRow(_audioEntries, 2);
        root.Children.Add(_audioEntries);
        return root;
    }

    private DataTemplate CreateAudioEntryTemplate()
    {
        var template = new DataTemplate();
        template.SetVisualTree(() =>
        {
            var container = new Grid();
            var header = CreateGroupHeader();
            header.SetBinding(VisibilityProperty, "HeaderVisibility");
            container.Children.Add(header);
            var row = new Grid { Margin = new Thickness(6, 3, 6, 3) };
            row.SetBinding(VisibilityProperty, "EntryVisibility");
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, new GridLength(58),
                         GridLength.Auto, new GridLength(1, GridUnitType.Star),
                         GridLength.Auto, new GridLength(120) })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var play = new Button { Content = Icon("\uE768"), Width = 24, Height = 24 };
            play.Click += (_, _) =>
            {
                if (play.DataContext is GroupedListRow<AudioRow> { Entry: { } entry } item)
                {
                    _audioEntries.SelectedItem = item;
                    _ = ToggleAudioAsync(entry.Model);
                }
            };
            play.ToolTip = _localization["ModelPreviewPage.AudioPlay"];
            row.Children.Add(play);
            AddText(1, "DisplayTitle", new Thickness(6, 0, 10, 0), fontFamily: "Consolas");
            AddText(2, "Origin", new Thickness(0), secondary: true);
            AddText(3, "Size", new Thickness(0, 0, 10, 0), secondary: true);
            AddText(4, "Format", new Thickness(0), secondary: true);
            AddText(5, "State", new Thickness(10, 0, 0, 0));
            AddText(6, "Issue", new Thickness(10, 0, 0, 0), secondary: true);
            container.Children.Add(row);
            return container;

            void AddText(int column, string property, Thickness margin,
                bool secondary = false, string? fontFamily = null)
            {
                var label = new TextBlock { FontSize = 11,
                    Foreground = secondary ? Secondary : Foreground,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center, Margin = margin };
                if (fontFamily is not null)
                    label.FontFamily = new FontFamily(fontFamily);
                label.SetBinding(TextBlock.TextProperty, $"Entry.{property}");
                Grid.SetColumn(label, column);
                row.Children.Add(label);
            }
        });
        return template;
    }

    private void ClearAudio()
    {
        StopAudio();
        _audioInventory = AudioInventoryResult.Empty;
        _audioRows = [];
        _audioEntries.ItemsSource = null;
        _audioModifiedOnly.IsChecked = false;
        _audioMessage.Text = string.Empty;
        _tabs[3].Visibility = Visibility.Collapsed;
        if (_previewTabs.SelectedIndex == 3)
            _previewTabs.SelectedIndex = 0;
    }

    private void ApplyAudio(AudioInventoryResult inventory, PatchResourceInspectionResult resources)
    {
        _audioInventory = inventory;
        _audioRows = inventory.Groups.SelectMany(group => group.Entries)
            .Select(entry => new AudioRow(entry, CreateAudioGroupKey(entry), entry.SourceId.ToString(), entry.PatchRelativePath,
                entry.BankName ?? string.Empty,
                entry.Origin == AudioEntryOrigin.BankMedia ? "Bank" : "Stream",
                entry.SampleRate > 0 ? $"{entry.SampleRate / 1000.0:0.#} kHz · {entry.Channels}ch" : "-",
                FormatAudioSize(entry.SizeBytes),
                entry.MatchesOriginal == false ? _localization["ModelPreviewPage.AudioModifiedTag"]
                    : entry.MatchesOriginal == true ? _localization["ModelPreviewPage.AudioOriginalTag"] : string.Empty,
                GetAudioIssue(entry.Issue)))
            .ToArray();
        _tabs[3].Visibility = _audioRows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var comparable = _audioRows.Any(row => row.Model.MatchesOriginal is not null);
        _audioModifiedOnly.Visibility = comparable ? Visibility.Visible : Visibility.Collapsed;
        _audioModifiedCount.Visibility = _audioModifiedOnly.Visibility;
        RefreshAudioTexts();
        BindAudioRows();
        if (!string.IsNullOrWhiteSpace(inventory.Error))
        {
            _audioMessage.Text = _localization["ModelPreviewPage.AudioLoadFailed"]
                .Replace("{message}", inventory.Error);
            _status.Text += " " + _audioMessage.Text;
        }
        if (inventory.UncomparedEntries > 0)
            _audioMessage.Text += " " + _localization["ModelPreviewPage.AudioUncomparedHint"]
                .Replace("{count}", inventory.UncomparedEntries.ToString("N0"));
        if (_audioRows.Length > 0 && !resources.TocEntries.Any(entry => entry.TypeId == PatchResourceTypeIds.Unit))
            _previewTabs.SelectedIndex = 3;
    }

    private string GetAudioIssue(AudioEntryIssue issue) => issue switch
    {
        AudioEntryIssue.NotRiff => _localization["ModelPreviewPage.AudioIssueNotRiff"],
        AudioEntryIssue.NotVorbis => _localization["ModelPreviewPage.AudioIssueNotVorbis"],
        AudioEntryIssue.Truncated => _localization["ModelPreviewPage.AudioIssueTruncated"],
        AudioEntryIssue.ReadFailed => _localization["ModelPreviewPage.AudioIssueReadFailed"],
        _ => string.Empty,
    };

    private AudioGroupKey CreateAudioGroupKey(AudioEntry entry) => new(
        entry.BankName is { Length: > 0 } name ? name
            : entry.Origin == AudioEntryOrigin.BankMedia ? $"Bank 0x{entry.BankFileId:X16}"
            : _localization["ModelPreviewPage.AudioLooseStreamsGroup"],
        entry.PatchRelativePath);

    private static string FormatAudioSize(long size) => size >= 1_048_576
        ? $"{size / 1_048_576d:0.#} MB" : size >= 1024
            ? $"{size / 1024d:0.#} KB" : $"{size} B";

    private void BindAudioRows(AudioEntry? selected = null)
    {
        var visible = _audioRows.Where(FilterAudioRow)
            .GroupBy(row => row.GroupKey)
            .SelectMany(group => new[] { new GroupedListRow<AudioRow>(null,
                    group.Key.Header, group.Key.PatchPath) }
                .Concat(group.Select(row => new GroupedListRow<AudioRow>(row,
                    group.Key.Header, group.Key.PatchPath))))
            .ToArray();
        _audioEntries.ItemsSource = visible;
        _audioEntries.SelectedItem = selected is null ? null
            : visible.FirstOrDefault(row => ReferenceEquals(row.Entry?.Model, selected));
    }

    private bool FilterAudioRow(AudioRow row)
    {
        var query = _audioFilter.Text.Trim();
        return (_audioModifiedOnly.IsChecked != true || row.Model.MatchesOriginal == false)
            && (query.Length == 0 || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Patch.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Bank.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Origin.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshAudioRowsLocalization()
    {
        if (_audioRows.Length == 0)
            return;
        var selected = (_audioEntries.SelectedItem as GroupedListRow<AudioRow>)?.Entry?.Model;
        _audioRows = _audioRows.Select(row => row with
        {
            GroupKey = CreateAudioGroupKey(row.Model),
            State = row.Model.MatchesOriginal == false ? _localization["ModelPreviewPage.AudioModifiedTag"]
                : row.Model.MatchesOriginal == true ? _localization["ModelPreviewPage.AudioOriginalTag"] : string.Empty,
            Issue = GetAudioIssue(row.Model.Issue),
        }).ToArray();
        BindAudioRows(selected);
    }

    private void ApplyAudioFilter()
    {
        BindAudioRows((_audioEntries.SelectedItem as GroupedListRow<AudioRow>)?.Entry?.Model);
    }

    private void RefreshAudioTexts()
    {
        _audioPlayButton.Content = Icon(_audioPlayer.State == AudioPlaybackState.Playing ? "\uE769" : "\uE768");
        _audioPlayButton.ToolTip = _localization[_audioPlayer.State == AudioPlaybackState.Playing
            ? "ModelPreviewPage.AudioPause" : "ModelPreviewPage.AudioPlay"];
        _audioStopButton.Content = Icon("\uE71A");
        _audioStopButton.ToolTip = _localization["ModelPreviewPage.AudioStop"];
        _audioFilter.ToolTip = _localization["ModelPreviewPage.AudioFilterTip"];
        _audioVolume.ToolTip = _localization["ModelPreviewPage.AudioVolume"];
        _audioModifiedOnly.Content = _localization["ModelPreviewPage.AudioOnlyShowModified"];
        _audioModifiedOnly.ToolTip = _localization["ModelPreviewPage.AudioOnlyShowModifiedTip"];
        _audioCount.Text = _localization["ModelPreviewPage.AudioEntryCount"]
            .Replace("{count}", _audioRows.Length.ToString("N0"))
            .Replace("{banks}", _audioInventory.Groups.Count(group => group.BankName is not null).ToString("N0"));
        _audioModifiedCount.Text = _localization["ModelPreviewPage.AudioModifiedCount"]
            .Replace("{modified}", _audioRows.Count(row => row.Model.MatchesOriginal == false).ToString("N0"))
            .Replace("{total}", _audioRows.Length.ToString("N0"));
    }

    private async Task ToggleAudioAsync(AudioEntry? requested = null)
    {
        var entry = requested ?? (_audioEntries.SelectedItem as GroupedListRow<AudioRow>)?.Entry?.Model
            ?? _currentAudioEntry;
        if (_disposed || entry is null)
            return;
        if (ReferenceEquals(entry, _currentAudioEntry))
        {
            if (_audioPlayer.State == AudioPlaybackState.Playing)
                _audioPlayer.Pause();
            else if (_audioPlayer.State == AudioPlaybackState.Paused)
                _audioPlayer.Resume();
            else
                return;
            RefreshAudioTexts();
            return;
        }
        if (!entry.IsPlayable)
        {
            _audioMessage.Text = GetAudioIssue(entry.Issue);
            return;
        }
        StopAudio();
        var generation = _audioPlaybackGeneration;
        var selectedMod = _selectedMod;
        using var cancellation = new CancellationTokenSource();
        _audioPlaybackCancellation = cancellation;
        _audioPlayButton.IsEnabled = false;
        try
        {
            var (success, error) = await _audioPlayer.PlayAsync(entry, cancellation.Token);
            if (_disposed || generation != _audioPlaybackGeneration || !ReferenceEquals(selectedMod, _selectedMod))
                return;
            if (success)
            {
                _currentAudioEntry = entry;
                _audioPlayer.SetVolume((float)(_audioVolume.Value / 100));
                _audioPosition.Maximum = Math.Max(1, _audioPlayer.Duration.TotalSeconds);
                _audioTimer.Change(200, 200);
                UpdateAudioPosition();
            }
            else if (error is not null)
                _audioMessage.Text = _localization["ModelPreviewPage.AudioPlaybackFailed"]
                    .Replace("{message}", error);
            RefreshAudioTexts();
        }
        catch (Exception ex)
        {
            if (!_disposed && generation == _audioPlaybackGeneration)
                _audioMessage.Text = _localization["ModelPreviewPage.AudioPlaybackFailed"]
                    .Replace("{message}", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_audioPlaybackCancellation, cancellation))
                _audioPlaybackCancellation = null;
            if (!_disposed && generation == _audioPlaybackGeneration)
                _audioPlayButton.IsEnabled = true;
        }
    }

    private void StopAudio()
    {
        _audioPlaybackGeneration++;
        _audioPlaybackCancellation?.Cancel();
        _audioTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _audioPlayer.Stop();
        _currentAudioEntry = null;
        _updatingAudioPosition = true;
        _audioPosition.Value = 0;
        _audioPosition.Maximum = 1;
        _updatingAudioPosition = false;
        _audioTime.Text = "00:00 / 00:00";
        _audioPlayButton.IsEnabled = true;
        RefreshAudioTexts();
    }

    private void UpdateAudioPosition()
    {
        if (_disposed || _currentAudioEntry is null)
            return;
        var position = _audioPlayer.Position;
        var duration = _audioPlayer.Duration;
        _updatingAudioPosition = true;
        _audioPosition.Maximum = Math.Max(1, duration.TotalSeconds);
        _audioPosition.Value = Math.Clamp(position.TotalSeconds, 0, _audioPosition.Maximum);
        _updatingAudioPosition = false;
        _audioTime.Text = $"{position:mm\\:ss} / {duration:mm\\:ss}";
    }

    private void OnAudioPlaybackEnded(AudioEntry entry, string? error)
    {
        _audioDispatcher.BeginInvoke(() =>
        {
            if (_disposed || !ReferenceEquals(_currentAudioEntry, entry))
                return;
            StopAudio();
            if (error is not null)
                _audioMessage.Text = _localization["ModelPreviewPage.AudioPlaybackFailed"]
                    .Replace("{message}", error);
        });
    }

    private void DisposeAudio()
    {
        _audioPlaybackGeneration++;
        _audioPlaybackCancellation?.Cancel();
        _audioTimer.Dispose();
        _audioPlayer.PlaybackEnded -= OnAudioPlaybackEnded;
        _audioPlayer.Dispose();
    }

    private sealed record AudioGroupKey(string Header, string PatchPath);

    private sealed record AudioRow(AudioEntry Model, AudioGroupKey GroupKey, string Id,
        string Patch, string Bank, string Origin, string Format, string Size, string State, string Issue)
    {
        public string DisplayTitle => $"#{Id}";
    }
}
