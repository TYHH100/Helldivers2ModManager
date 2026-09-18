using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class FloatingMusicPlayerViewModel : ObservableObject, IDisposable
{
	private readonly BackgroundMusicService _musicService;
	private readonly LocalizationService _localizationService;
	private readonly SettingsService _settingsService;
	private readonly DispatcherTimer _timer;
	private readonly DispatcherTimer _preferencesSaveTimer;
	private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
	private bool _initialized;
	private bool _updatingSelection;
	private bool _restoringPreferences;
	private bool _disposed;

	[ObservableProperty]
	private bool _isExpanded;
	[ObservableProperty]
	private bool _isPlaying;
	[ObservableProperty]
	private string _currentTrackName = string.Empty;
	[ObservableProperty]
	private double _currentPosition;
	[ObservableProperty]
	private double _totalDuration;
	[ObservableProperty]
	private string _currentPositionText = "0:00";
	[ObservableProperty]
	private string _totalDurationText = "0:00";
	[ObservableProperty]
	private double _volume = 30;
	[ObservableProperty]
	private bool _hasTrack;
	[ObservableProperty]
	private int _selectedTrackIndex = -1;
	[ObservableProperty]
	private string? _playbackError;
	[ObservableProperty]
	private MusicPlaybackMode _playbackMode = MusicPlaybackMode.Sequential;

	public ObservableCollection<string> Tracks { get; } = [];
	public bool HasPlaybackError => !string.IsNullOrWhiteSpace(PlaybackError);
	public string PlaybackModeIcon => PlaybackMode switch
	{
		MusicPlaybackMode.Loop => "\uE8EE",
		MusicPlaybackMode.Shuffle => "\uE8B1",
		_ => "\uE8FD",
	};
	public string PlaybackModeDisplayName => _localizationService[$"MusicPlayer.PlaybackMode.{PlaybackMode}"];
	public double SavedHorizontalPosition => _settingsService.MusicPlayerHorizontalPosition;
	public double SavedVerticalPosition => _settingsService.MusicPlayerVerticalPosition;
	public bool IsSeeking { get; set; }

	public FloatingMusicPlayerViewModel(
		BackgroundMusicService musicService,
		LocalizationService localizationService,
		SettingsService settingsService)
	{
		_musicService = musicService;
		_localizationService = localizationService;
		_settingsService = settingsService;
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
		_timer.Tick += Timer_Tick;
		_preferencesSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
		_preferencesSaveTimer.Tick += PreferencesSaveTimer_Tick;
		_musicService.PlayStateChanged += MusicService_StateChanged;
		_musicService.TrackChanged += MusicService_TrackChanged;
		_musicService.PlaylistChanged += MusicService_PlaylistChanged;
	}

	public void Initialize(bool autoPlay = false)
	{
		if (!_initialized)
		{
			_initialized = true;
			_restoringPreferences = true;
			try
			{
				PlaybackMode = _settingsService.MusicPlayerPlaybackMode;
				_musicService.SetPlaybackMode(PlaybackMode);
				_musicService.RefreshPlaylist(_settingsService.LastMusicTrackRelativePath);
				UpdatePlaylist();
				RestoreSavedVolume();
			}
			finally
			{
				_restoringPreferences = false;
			}
			SchedulePreferencesSave();
		}
		_timer.Start();
		if (autoPlay && HasTrack && !_musicService.IsPlaying)
			PlayPause();
	}

	public void SetEnabled(bool enabled, bool autoPlay = false)
	{
		if (enabled)
		{
			Initialize(autoPlay);
			return;
		}

		_timer.Stop();
		_musicService.Stop();
		UpdateState();
	}

	private void Timer_Tick(object? sender, EventArgs e)
	{
		if (!IsSeeking)
			UpdatePosition();
		UpdateState();
	}

	private async void PreferencesSaveTimer_Tick(object? sender, EventArgs e)
	{
		_preferencesSaveTimer.Stop();
		try
		{
			await SavePlaybackPreferencesAsync(notifyListeners: true);
		}
		catch
		{
			// 自动保存失败不影响当前播放；下一次状态变化时会重试。
		}
	}

	private void MusicService_StateChanged(object? sender, EventArgs e) => RunOnUi(UpdateState);

	private void MusicService_TrackChanged(object? sender, EventArgs e) => RunOnUi(() =>
	{
		UpdateState();
		SchedulePreferencesSave();
	});

	private void MusicService_PlaylistChanged(object? sender, EventArgs e) => RunOnUi(UpdatePlaylist);

	private void RunOnUi(Action action)
	{
		if (_timer.Dispatcher.CheckAccess())
			action();
		else
			_timer.Dispatcher.BeginInvoke(action);
	}

	private void UpdatePlaylist()
	{
		var names = _musicService.TrackNames;
		Tracks.Clear();
		foreach (var name in names)
			Tracks.Add(name);
		UpdateState();
	}

	private void UpdateState()
	{
		var current = _musicService.CurrentTrackName;
		CurrentTrackName = current ?? _localizationService["MusicPlayer.NoTracks"];
		HasTrack = current is not null;
		IsPlaying = _musicService.IsPlaying;
		TotalDuration = _musicService.Duration.TotalSeconds;
		TotalDurationText = FormatTime(_musicService.Duration);
		PlaybackError = _musicService.LastError;
		OnPropertyChanged(nameof(HasPlaybackError));

		_updatingSelection = true;
		SelectedTrackIndex = _musicService.CurrentTrackIndex;
		_updatingSelection = false;
	}

	private void UpdatePosition()
	{
		CurrentPosition = _musicService.Position.TotalSeconds;
		CurrentPositionText = FormatTime(_musicService.Position);
	}

	private static string FormatTime(TimeSpan time) =>
		time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

	partial void OnVolumeChanged(double value)
	{
		_musicService.SetVolume(value / 100.0);
		SchedulePreferencesSave();
	}

	partial void OnPlaybackModeChanged(MusicPlaybackMode value)
	{
		_musicService.SetPlaybackMode(value);
		OnPropertyChanged(nameof(PlaybackModeIcon));
		OnPropertyChanged(nameof(PlaybackModeDisplayName));
		SchedulePreferencesSave();
	}

	private void SchedulePreferencesSave()
	{
		if (_restoringPreferences || !_initialized)
			return;
		_preferencesSaveTimer.Stop();
		_preferencesSaveTimer.Start();
	}

	partial void OnPlaybackErrorChanged(string? value) => OnPropertyChanged(nameof(HasPlaybackError));

	partial void OnCurrentPositionChanged(double value)
	{
		if (!IsSeeking)
			return;
		_musicService.Seek(TimeSpan.FromSeconds(value));
		CurrentPositionText = FormatTime(TimeSpan.FromSeconds(value));
	}

	partial void OnSelectedTrackIndexChanged(int value)
	{
		if (!_updatingSelection && value >= 0)
		{
			_musicService.PlayTrackAt(value);
			UpdateState();
		}
	}

	[RelayCommand]
	private void PlayPause()
	{
		if (_musicService.IsPlaying)
			_musicService.Pause();
		else
			_musicService.Play();
		UpdateState();
	}

	[RelayCommand]
	private void Previous()
	{
		_musicService.Previous();
		UpdateState();
		UpdatePosition();
	}

	[RelayCommand]
	private void Next()
	{
		_musicService.Next();
		UpdateState();
		UpdatePosition();
	}

	[RelayCommand]
	private void RefreshPlaylist()
	{
		_musicService.RefreshPlaylist();
		UpdatePlaylist();
	}

	[RelayCommand]
	private void CyclePlaybackMode()
	{
		PlaybackMode = PlaybackMode switch
		{
			MusicPlaybackMode.Sequential => MusicPlaybackMode.Loop,
			MusicPlaybackMode.Loop => MusicPlaybackMode.Shuffle,
			_ => MusicPlaybackMode.Sequential,
		};
	}

	[RelayCommand]
	private void OpenMusicFolder()
	{
		Directory.CreateDirectory(_musicService.MusicDirectory);
		Process.Start(new ProcessStartInfo(_musicService.MusicDirectory) { UseShellExecute = true });
	}

	[RelayCommand]
	private void Expand() => IsExpanded = true;

	[RelayCommand]
	private void Collapse() => IsExpanded = false;

	private void RestoreSavedVolume()
	{
		var wasRestoring = _restoringPreferences;
		_restoringPreferences = true;
		try
		{
			Volume = _settingsService.MusicPlayerVolume * 100;
		}
		finally
		{
			_restoringPreferences = wasRestoring;
		}
	}

	public async Task SavePositionAsync(double horizontal, double vertical)
	{
		if (_settingsService.IsReadonly)
			return;
		await _settingsSaveGate.WaitAsync();
		try
		{
			_settingsService.MusicPlayerHorizontalPosition = Math.Clamp(horizontal, 0, 1);
			_settingsService.MusicPlayerVerticalPosition = Math.Clamp(vertical, 0, 1);
			await _settingsService.SaveAsync();
		}
		finally
		{
			_settingsSaveGate.Release();
		}
	}

	private async Task SavePlaybackPreferencesAsync(bool notifyListeners)
	{
		if (_settingsService.IsReadonly)
			return;
		await _settingsSaveGate.WaitAsync();
		try
		{
			_settingsService.MusicPlayerVolume = Math.Clamp(Volume / 100.0, 0, 1);
			_settingsService.MusicPlayerPlaybackMode = PlaybackMode;
			_settingsService.LastMusicTrackRelativePath = _musicService.CurrentTrackRelativePath ?? string.Empty;
			await _settingsService.SaveAsync(notifyListeners);
		}
		finally
		{
			_settingsSaveGate.Release();
		}
	}

	public Task FlushPlaybackPreferencesAsync()
	{
		_preferencesSaveTimer.Stop();
		return _initialized && !_settingsService.IsReadonly
			? SavePlaybackPreferencesAsync(notifyListeners: false)
			: Task.CompletedTask;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_timer.Stop();
		_timer.Tick -= Timer_Tick;
		_preferencesSaveTimer.Stop();
		_preferencesSaveTimer.Tick -= PreferencesSaveTimer_Tick;
		_musicService.PlayStateChanged -= MusicService_StateChanged;
		_musicService.TrackChanged -= MusicService_TrackChanged;
		_musicService.PlaylistChanged -= MusicService_PlaylistChanged;
		_disposed = true;
	}
}
