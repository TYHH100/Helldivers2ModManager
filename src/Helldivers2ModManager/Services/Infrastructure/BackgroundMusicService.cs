using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NVorbis;
using System.IO;

namespace Helldivers2ModManager.Services.Infrastructure;

internal enum MusicPlaybackMode
{
	Sequential,
	Loop,
	Shuffle,
}

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class BackgroundMusicService : IDisposable
{
	private static readonly HashSet<string> s_supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".mp3", ".wav", ".ogg", ".flac", ".m4a", ".wma"
	};

	private readonly ILogger<BackgroundMusicService> _logger;
	private readonly object _gate = new();
	private readonly List<string> _playlist = [];
	private IWavePlayer? _output;
	private IMusicSource? _source;
	private int _currentTrackIndex = -1;
	private MusicPlaybackMode _playbackMode = MusicPlaybackMode.Sequential;
	private bool _isPlaying;
	private bool _disposed;

	public event EventHandler? PlayStateChanged;
	public event EventHandler? TrackChanged;
	public event EventHandler? PlaylistChanged;
	public event EventHandler? VolumeChanged;

	public string MusicDirectory { get; }
	public bool IsPlaying { get { lock (_gate) return _isPlaying; } }
	public bool HasTrack { get { lock (_gate) return HasTrackCore(); } }
	public TimeSpan Position { get { lock (_gate) return _source?.Position ?? TimeSpan.Zero; } }
	public TimeSpan Duration { get { lock (_gate) return _source?.Duration ?? TimeSpan.Zero; } }
	public double Volume { get; private set; } = 0.3;
	public string? LastError { get; private set; }

	public string? CurrentTrackName
	{
		get
		{
			lock (_gate)
				return HasTrackCore() ? Path.GetFileNameWithoutExtension(_playlist[_currentTrackIndex]) : null;
		}
	}

	public int CurrentTrackIndex { get { lock (_gate) return _currentTrackIndex; } }
	public MusicPlaybackMode PlaybackMode { get { lock (_gate) return _playbackMode; } }
	public string? CurrentTrackRelativePath
	{
		get
		{
			lock (_gate)
				return HasTrackCore() ? Path.GetRelativePath(MusicDirectory, _playlist[_currentTrackIndex]) : null;
		}
	}
	public IReadOnlyList<string> TrackNames
	{
		get
		{
			lock (_gate)
				return _playlist.Select(static path => Path.GetFileNameWithoutExtension(path)!).ToArray();
		}
	}

	public BackgroundMusicService(ILogger<BackgroundMusicService> logger)
		: this(logger, Path.Combine(AppContext.BaseDirectory, "Music"))
	{
	}

	internal BackgroundMusicService(ILogger<BackgroundMusicService> logger, string musicDirectory)
	{
		_logger = logger;
		MusicDirectory = musicDirectory;
	}

	public bool EnsureMusicDirectory()
	{
		try
		{
			Directory.CreateDirectory(MusicDirectory);
			LastError = null;
			return true;
		}
		catch (Exception ex)
		{
			LastError = ex.Message;
			_logger.LogError(ex, "Failed to create music directory {Directory}", MusicDirectory);
			return false;
		}
	}

	public void RefreshPlaylist(string? preferredRelativePath = null)
	{
		List<string> musicFiles = [];
		try
		{
			if (!EnsureMusicDirectory())
				return;
			musicFiles = Directory.EnumerateFiles(MusicDirectory, "*.*", SearchOption.AllDirectories)
				.Where(static file => s_supportedExtensions.Contains(Path.GetExtension(file)))
				.OrderBy(static file => Path.GetFileNameWithoutExtension(file), StringComparer.CurrentCultureIgnoreCase)
				.ThenBy(static file => file, StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to refresh music playlist from {Directory}", MusicDirectory);
			LastError = ex.Message;
		}

		bool trackChanged;
		lock (_gate)
		{
			var currentPath = HasTrackCore() ? _playlist[_currentTrackIndex] : null;
			_playlist.Clear();
			_playlist.AddRange(musicFiles);

			var preservedIndex = currentPath is null ? -1 : FindTrackIndexCore(currentPath, relative: false);
			if (preservedIndex < 0 && !string.IsNullOrWhiteSpace(preferredRelativePath))
				preservedIndex = FindTrackIndexCore(preferredRelativePath, relative: true);
			trackChanged = preservedIndex < 0 && currentPath is not null;
			if (trackChanged)
				ClosePlaybackCore();
			_currentTrackIndex = preservedIndex >= 0 ? preservedIndex : (_playlist.Count > 0 ? 0 : -1);
		}

		_logger.LogInformation("Loaded {Count} music files from {Directory}", musicFiles.Count, MusicDirectory);
		PlaylistChanged?.Invoke(this, EventArgs.Empty);
		TrackChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool Play()
	{
		lock (_gate)
		{
			if (!HasTrackCore())
			{
				LastError = null;
				return false;
			}

			try
			{
				if (_output is null || _source is null)
					OpenCurrentTrackCore();
				_output!.Play();
				_isPlaying = true;
				LastError = null;
			}
			catch (Exception ex)
			{
				ClosePlaybackCore();
				LastError = ex.Message;
				_logger.LogError(ex, "Failed to play music track {Track}", _playlist[_currentTrackIndex]);
				PlayStateChanged?.Invoke(this, EventArgs.Empty);
				return false;
			}
		}

		TrackChanged?.Invoke(this, EventArgs.Empty);
		PlayStateChanged?.Invoke(this, EventArgs.Empty);
		return true;
	}

	public void Pause()
	{
		lock (_gate)
		{
			_output?.Pause();
			_isPlaying = false;
		}
		PlayStateChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Stop()
	{
		lock (_gate)
			ClosePlaybackCore();
		PlayStateChanged?.Invoke(this, EventArgs.Empty);
	}

	public void Next() => ChangeTrack(next: true, IsPlaying);
	public void Previous() => ChangeTrack(next: false, IsPlaying);

	public void SetPlaybackMode(MusicPlaybackMode mode)
	{
		if (!Enum.IsDefined(mode))
			mode = MusicPlaybackMode.Sequential;
		lock (_gate)
			_playbackMode = mode;
	}

	public void PlayTrackAt(int index)
	{
		lock (_gate)
		{
			if (index < 0 || index >= _playlist.Count)
				return;
			ClosePlaybackCore();
			_currentTrackIndex = index;
		}
		TrackChanged?.Invoke(this, EventArgs.Empty);
		Play();
	}

	public void Seek(TimeSpan position)
	{
		lock (_gate)
		{
			if (_source is null)
				return;
			_source.Position = position < TimeSpan.Zero
				? TimeSpan.Zero
				: position > _source.Duration ? _source.Duration : position;
		}
	}

	public void SetVolume(double volume)
	{
		Volume = Math.Clamp(volume, 0.0, 1.0);
		lock (_gate)
		{
			if (_output is not null)
				_output.Volume = (float)Volume;
		}
		VolumeChanged?.Invoke(this, EventArgs.Empty);
	}

	private void ChangeTrack(bool next, bool startPlaying)
	{
		int? targetIndex;
		lock (_gate)
		{
			if (_playlist.Count == 0)
				return;
			targetIndex = next ? GetNextTrackIndexCore() : GetPreviousTrackIndexCore();
			if (targetIndex is null)
			{
				ClosePlaybackCore();
			}
			else
			{
				ClosePlaybackCore();
				_currentTrackIndex = targetIndex.Value;
			}
		}

		if (targetIndex is not null)
			TrackChanged?.Invoke(this, EventArgs.Empty);
		if (startPlaying && targetIndex is not null)
			Play();
		else
			PlayStateChanged?.Invoke(this, EventArgs.Empty);
	}

	private void OpenCurrentTrackCore()
	{
		var trackPath = _playlist[_currentTrackIndex];
		_source = Path.GetExtension(trackPath).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
			? new VorbisMusicSource(trackPath)
			: new WaveStreamMusicSource(CreateWaveStream(trackPath));
		_output = new WaveOutEvent { DesiredLatency = 200 };
		_output.Init(_source.WaveProvider);
		_output.Volume = (float)Volume;
		_output.PlaybackStopped += Output_PlaybackStopped;
		_logger.LogInformation("Opened music track: {Track}", Path.GetFileName(trackPath));
	}

	private static WaveStream CreateWaveStream(string path)
	{
		return Path.GetExtension(path).ToLowerInvariant() switch
		{
			".wav" => new WaveFileReader(path),
			".mp3" => new Mp3FileReader(path),
			_ => new MediaFoundationReader(path),
		};
	}

	private void Output_PlaybackStopped(object? sender, StoppedEventArgs e)
	{
		bool playNext;
		lock (_gate)
		{
			if (!ReferenceEquals(sender, _output))
				return;
			if (e.Exception is not null)
			{
				LastError = e.Exception.Message;
				_logger.LogError(e.Exception, "Music playback stopped unexpectedly");
			}
			var nextIndex = e.Exception is null && _isPlaying ? GetNextTrackIndexCore() : null;
			playNext = nextIndex is not null;
			ClosePlaybackCore();
			if (playNext)
				_currentTrackIndex = nextIndex!.Value;
		}

		TrackChanged?.Invoke(this, EventArgs.Empty);
		PlayStateChanged?.Invoke(this, EventArgs.Empty);
		if (playNext)
			Play();
	}

	private bool HasTrackCore() => _currentTrackIndex >= 0 && _currentTrackIndex < _playlist.Count;

	private int FindTrackIndexCore(string path, bool relative)
	{
		return _playlist.FindIndex(candidate => string.Equals(
			relative ? Path.GetRelativePath(MusicDirectory, candidate) : candidate,
			path,
			StringComparison.OrdinalIgnoreCase));
	}

	private int? GetNextTrackIndexCore()
	{
		if (!HasTrackCore() || _playlist.Count == 0)
			return null;
		if (_playbackMode == MusicPlaybackMode.Shuffle)
			return _playlist.Count == 1 ? _currentTrackIndex : GetDifferentRandomIndexCore();
		if (_currentTrackIndex + 1 < _playlist.Count)
			return _currentTrackIndex + 1;
		return _playbackMode == MusicPlaybackMode.Loop ? 0 : null;
	}

	private int? GetPreviousTrackIndexCore()
	{
		if (!HasTrackCore() || _playlist.Count == 0)
			return null;
		if (_playbackMode == MusicPlaybackMode.Shuffle)
			return _playlist.Count == 1 ? _currentTrackIndex : GetDifferentRandomIndexCore();
		if (_currentTrackIndex > 0)
			return _currentTrackIndex - 1;
		return _playbackMode == MusicPlaybackMode.Loop ? _playlist.Count - 1 : null;
	}

	private int GetDifferentRandomIndexCore()
	{
		var index = Random.Shared.Next(_playlist.Count - 1);
		return index >= _currentTrackIndex ? index + 1 : index;
	}

	private void ClosePlaybackCore()
	{
		var output = _output;
		var source = _source;
		_output = null;
		_source = null;
		_isPlaying = false;
		if (output is not null)
		{
			output.PlaybackStopped -= Output_PlaybackStopped;
			try { output.Stop(); } catch { }
			output.Dispose();
		}
		source?.Dispose();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		lock (_gate)
			ClosePlaybackCore();
		_disposed = true;
	}

	private interface IMusicSource : IDisposable
	{
		IWaveProvider WaveProvider { get; }
		TimeSpan Position { get; set; }
		TimeSpan Duration { get; }
	}

	private sealed class WaveStreamMusicSource(WaveStream reader) : IMusicSource
	{
		public IWaveProvider WaveProvider => reader;
		public TimeSpan Position { get => reader.CurrentTime; set => reader.CurrentTime = value; }
		public TimeSpan Duration => reader.TotalTime;
		public void Dispose() => reader.Dispose();
	}

	private sealed class VorbisMusicSource : IMusicSource
	{
		private readonly VorbisReader _reader;
		public VorbisMusicSource(string path)
		{
			_reader = new VorbisReader(path);
			WaveProvider = new SampleToWaveProvider16(new VorbisSampleProvider(_reader));
		}
		public IWaveProvider WaveProvider { get; }
		public TimeSpan Position
		{
			get => TimeSpan.FromSeconds(_reader.SamplePosition / (double)_reader.SampleRate);
			set => _reader.SamplePosition = (long)(value.TotalSeconds * _reader.SampleRate);
		}
		public TimeSpan Duration => _reader.TotalTime;
		public void Dispose() => _reader.Dispose();
	}

	private sealed class VorbisSampleProvider(VorbisReader reader) : ISampleProvider
	{
		public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
		public int Read(float[] buffer, int offset, int count) => reader.ReadSamples(buffer, offset, count);
	}
}
