using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

/// <summary>
/// Jalium 版浮动音乐播放器。播放、曲目扫描和偏好保存仍由原 BackgroundMusicService 负责。
/// </summary>
internal sealed class FloatingMusicPlayerView : Grid, IDisposable
{
    private static readonly Brush Foreground = Paint(0xFF, 0xFF, 0xFF);
    private static readonly Brush Secondary = Paint(0xCF, 0xCF, 0xCF);
    private static readonly Brush Surface = Paint(0x1E, 0x1E, 0x1E, 0xF0);
    private static readonly Brush ButtonSurface = Paint(0x45, 0x45, 0x45, 0xD0);
    private static readonly Brush Accent = Paint(0x00, 0x78, 0xD4);
    private readonly BackgroundMusicService _music;
    private readonly SettingsService _settings;
    private readonly LocalizationService _localization;
    private readonly Dispatcher _dispatcher;
    private readonly Border _collapsed;
    private readonly Border _expanded;
    private readonly TextBlock _track = new();
    private readonly TextBlock _error = new();
    private readonly TextBlock _position = new();
    private readonly TextBlock _duration = new();
    private readonly TextBlock _volumeText = new();
    private readonly TextBlock _count = new();
    private readonly Slider _progress = new();
    private readonly Slider _volume = new();
    private readonly ComboBox _tracks = new();
    private Button _play = null!;
    private Button _mode = null!;
    private readonly StackPanel _empty = new();
    private readonly System.Threading.Timer _timer;
    private readonly System.Threading.Timer _preferencesTimer;
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private bool _seeking;
    private bool _updatingTrack;
    private bool _disposed;

    public FloatingMusicPlayerView(BackgroundMusicService music, SettingsService settings,
        LocalizationService localization)
    {
        _music = music;
        _settings = settings;
        _localization = localization;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Width = 360;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;

        _collapsed = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(28),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Surface, BorderBrush = Paint(0x55, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1), Child = new TextBlock
            {
                Text = "\uE8D6", FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 23, Foreground = Foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _collapsed.ToolTip = _localization["MusicPlayer.Expand"];
        _collapsed.MouseLeftButtonUp += (_, _) => SetExpanded(true);
        Children.Add(_collapsed);

        _expanded = BuildExpanded();
        _expanded.Visibility = Visibility.Collapsed;
        Children.Add(_expanded);

        _music.PlayStateChanged += OnMusicChanged;
        _music.TrackChanged += OnMusicChanged;
        _music.PlaylistChanged += OnPlaylistChanged;
        _localization.PropertyChanged += OnLocalizationChanged;
        _music.SetPlaybackMode(_settings.MusicPlayerPlaybackMode);
        _music.SetVolume(_settings.MusicPlayerVolume);
        _music.RefreshPlaylist(_settings.LastMusicTrackRelativePath);
        RefreshPlaylist();
        SetEnabled(_settings.EnableMusicPlayer, _settings.AutoPlayBackgroundMusic);
        _timer = new System.Threading.Timer(_ => _dispatcher.BeginInvoke(UpdateState),
            null, 200, 200);
        _preferencesTimer = new System.Threading.Timer(_ => _ = SavePreferencesAsync(),
            null, Timeout.Infinite, Timeout.Infinite);
    }

    public void SetEnabled(bool enabled, bool autoPlay = false)
    {
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled)
        {
            if (autoPlay && _music.HasTrack && !_music.IsPlaying)
                _music.Play();
            UpdateState();
        }
        else
        {
            _music.Stop();
            SetExpanded(false);
        }
    }

    private Border BuildExpanded()
    {
        var root = new Grid { Margin = new Thickness(14) };
        for (var i = 0; i < 7; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = i == 6 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = _localization["MusicPlayer.Title"], FontSize = 14,
            FontWeight = FontWeights.SemiBold, Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center });
        _mode = IconButton("\uE8FD", _localization["MusicPlayer.PlaybackMode.Sequential"]);
        _mode.Click += (_, _) =>
        {
            var next = _music.PlaybackMode switch
            {
                MusicPlaybackMode.Sequential => MusicPlaybackMode.Loop,
                MusicPlaybackMode.Loop => MusicPlaybackMode.Shuffle,
                _ => MusicPlaybackMode.Sequential,
            };
            _music.SetPlaybackMode(next);
            SchedulePreferencesSave();
            UpdateState();
        };
        Grid.SetColumn(_mode, 1); header.Children.Add(_mode);
        var folder = IconButton("\uE8B7", _localization["MusicPlayer.OpenFolder"]);
        folder.Click += (_, _) => OpenFolder(); Grid.SetColumn(folder, 2); header.Children.Add(folder);
        var refresh = IconButton("\uE72C", _localization["MusicPlayer.Refresh"]);
        refresh.Click += (_, _) => { _music.RefreshPlaylist(); RefreshPlaylist(); };
        Grid.SetColumn(refresh, 3); header.Children.Add(refresh);
        var close = IconButton("\uE70E", _localization["MusicPlayer.Collapse"]);
        close.Click += (_, _) => SetExpanded(false); Grid.SetColumn(close, 4); header.Children.Add(close);
        Grid.SetRow(header, 0); root.Children.Add(header);

        var trackPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        _track.FontSize = 14; _track.FontWeight = FontWeights.SemiBold; _track.Foreground = Foreground;
        _error.FontSize = 11; _error.Foreground = Paint(0xFF, 0x8A, 0x80); _error.TextWrapping = TextWrapping.Wrap;
        trackPanel.Children.Add(_track); trackPanel.Children.Add(_error);
        Grid.SetRow(trackPanel, 1); root.Children.Add(trackPanel);

        _progress.Minimum = 0; _progress.Maximum = 1; _progress.IsEnabled = false;
        _progress.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _progress.PreviewMouseLeftButtonUp += (_, _) => _seeking = false;
        _progress.ValueChanged += (_, _) => { if (_seeking) _music.Seek(TimeSpan.FromSeconds(_progress.Value)); };
        var progressPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        progressPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        progressPanel.Children.Add(_progress);
        _position.Foreground = Secondary; _position.FontSize = 11;
        _duration.Foreground = Secondary; _duration.FontSize = 11; _duration.HorizontalAlignment = HorizontalAlignment.Right;
        var times = new Grid(); times.Children.Add(_position); times.Children.Add(_duration);
        Grid.SetRow(times, 1); progressPanel.Children.Add(times);
        Grid.SetRow(progressPanel, 2); root.Children.Add(progressPanel);

        var controls = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        var previous = IconButton("\uE892", _localization["MusicPlayer.Previous"]);
        previous.Click += (_, _) => { _music.Previous(); UpdateState(); };
        controls.Children.Add(previous);
        _play = IconButton("\uE768", _localization["MusicPlayer.Play"]);
        _play.Width = 44; _play.Height = 44; _play.Background = Accent;
        _play.Click += (_, _) => { if (_music.IsPlaying) _music.Pause(); else _music.Play(); UpdateState(); };
        controls.Children.Add(_play);
        var next = IconButton("\uE893", _localization["MusicPlayer.Next"]);
        next.Click += (_, _) => { _music.Next(); UpdateState(); };
        controls.Children.Add(next);
        Grid.SetRow(controls, 3); root.Children.Add(controls);

        var volumePanel = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        volumePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        volumePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volumePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        var volumeIcon = new TextBlock { Text = "\uE767", FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center };
        volumePanel.Children.Add(volumeIcon);
        _volume.Minimum = 0; _volume.Maximum = 100; _volume.Value = _settings.MusicPlayerVolume * 100;
        _volume.ValueChanged += (_, _) =>
        {
            _music.SetVolume(_volume.Value / 100);
            _volumeText.Text = $"{_volume.Value:0}%";
            SchedulePreferencesSave();
        };
        Grid.SetColumn(_volume, 1); volumePanel.Children.Add(_volume);
        _volumeText.Foreground = Secondary; _volumeText.FontSize = 11; _volumeText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_volumeText, 2); volumePanel.Children.Add(_volumeText);
        Grid.SetRow(volumePanel, 4); root.Children.Add(volumePanel);

        var playlistHeader = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        playlistHeader.Children.Add(new TextBlock { Text = _localization["MusicPlayer.Playlist"],
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Foreground });
        _count.Foreground = Secondary; _count.FontSize = 11; _count.HorizontalAlignment = HorizontalAlignment.Right;
        playlistHeader.Children.Add(_count); Grid.SetRow(playlistHeader, 5); root.Children.Add(playlistHeader);

        _tracks.MaxHeight = 160; _tracks.SelectionChanged += (_, _) =>
        {
            if (!_updatingTrack && _tracks.SelectedIndex >= 0)
            {
                _music.PlayTrackAt(_tracks.SelectedIndex);
                SchedulePreferencesSave();
            }
        };
        var listBorder = new Border { Child = _tracks, BorderBrush = Paint(0x35, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1), Padding = new Thickness(4) };
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.Children.Add(new TextBlock { Text = _localization["MusicPlayer.NoTracks"], Foreground = Foreground,
            HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(new TextBlock { Text = _localization["MusicPlayer.NoTracksDesc"], Foreground = Secondary,
            FontSize = 11, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0) });
        var listRoot = new Grid(); listRoot.Children.Add(listBorder); listRoot.Children.Add(_empty);
        Grid.SetRow(listRoot, 6); root.Children.Add(listRoot);

        return new Border { Width = 360, Background = Surface, BorderBrush = Paint(0x55, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = root };
    }

    private Button IconButton(string glyph, string tooltip)
    {
        var button = new Button { Width = 32, Height = 32, Content = new TextBlock { Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = Foreground },
            Background = ButtonSurface, BorderBrush = Paint(0x55, 0xFF, 0xFF, 0xFF),
            BorderThickness = new Thickness(1), Margin = new Thickness(3, 0, 0, 0), ToolTip = tooltip };
        return button;
    }

    private void SetExpanded(bool expanded)
    {
        _collapsed.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        _expanded.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPlaylist()
    {
        _updatingTrack = true;
        _tracks.ItemsSource = _music.TrackNames.ToArray();
        _tracks.SelectedIndex = _music.CurrentTrackIndex;
        _updatingTrack = false;
        _count.Text = _music.TrackNames.Count.ToString();
        _empty.Visibility = _music.HasTrack ? Visibility.Collapsed : Visibility.Visible;
        _tracks.Visibility = _music.HasTrack ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void UpdateState()
    {
        if (_disposed) return;
        _track.Text = _music.CurrentTrackName ?? _localization["MusicPlayer.NoTracks"];
        _error.Text = _music.LastError ?? string.Empty;
        _play.Content = new TextBlock { Text = _music.IsPlaying ? "\uE769" : "\uE768",
            FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Foreground = Foreground };
        _play.IsEnabled = _music.HasTrack;
        _progress.IsEnabled = _music.HasTrack;
        var duration = _music.Duration.TotalSeconds;
        _progress.Maximum = Math.Max(1, duration);
        if (!_seeking) _progress.Value = Math.Min(_progress.Maximum, _music.Position.TotalSeconds);
        _position.Text = FormatTime(_music.Position);
        _duration.Text = FormatTime(_music.Duration);
        _volumeText.Text = $"{_volume.Value:0}%";
        _mode.Content = new TextBlock { Text = ModeIcon(), FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14, Foreground = Foreground };
        _mode.ToolTip = _localization[$"MusicPlayer.PlaybackMode.{_music.PlaybackMode}"];
        _count.Text = _music.TrackNames.Count.ToString();
    }

    private string ModeIcon() => _music.PlaybackMode switch
    {
        MusicPlaybackMode.Loop => "\uE8EE",
        MusicPlaybackMode.Shuffle => "\uE8B1",
        _ => "\uE8FD",
    };

    private void OpenFolder()
    {
        Directory.CreateDirectory(_music.MusicDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_music.MusicDirectory)
            { UseShellExecute = true });
    }

    private void SchedulePreferencesSave()
    {
        if (!_disposed)
            _preferencesTimer?.Change(400, Timeout.Infinite);
    }

    private async Task SavePreferencesAsync()
    {
        if (_settings.IsReadonly)
            return;
        await _settingsSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _settings.MusicPlayerVolume = Math.Clamp(_music.Volume, 0, 1);
            _settings.MusicPlayerPlaybackMode = _music.PlaybackMode;
            _settings.LastMusicTrackRelativePath = _music.CurrentTrackRelativePath ?? string.Empty;
            await _settings.SaveAsync(notifyListeners: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Music preferences save failed: {ex.Message}");
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void OnMusicChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(UpdateState);
    private void OnPlaylistChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(RefreshPlaylist);
    private void OnLocalizationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            _collapsed.ToolTip = _localization["MusicPlayer.Expand"];
            _empty.Children[0] = new TextBlock { Text = _localization["MusicPlayer.NoTracks"], Foreground = Foreground };
            _empty.Children[1] = new TextBlock { Text = _localization["MusicPlayer.NoTracksDesc"], Foreground = Secondary,
                FontSize = 11, TextWrapping = TextWrapping.Wrap };
            UpdateState();
        });

    private static SolidColorBrush Paint(byte r, byte g, byte b, byte a = 0xFF) =>
        new(Color.FromArgb(a, r, g, b));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _preferencesTimer.Dispose();
        Task.Run(SavePreferencesAsync).GetAwaiter().GetResult();
        _music.PlayStateChanged -= OnMusicChanged;
        _music.TrackChanged -= OnMusicChanged;
        _music.PlaylistChanged -= OnPlaylistChanged;
        _localization.PropertyChanged -= OnLocalizationChanged;
        _music.Stop();
        _music.Dispose();
    }
}
