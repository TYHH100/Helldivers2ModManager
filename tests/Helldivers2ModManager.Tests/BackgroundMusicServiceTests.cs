using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class BackgroundMusicServiceTests
{
	private string _tempDirectory = null!;
	private BackgroundMusicService _service = null!;

	[TestInitialize]
	public void Initialize()
	{
		_tempDirectory = Path.Combine(Path.GetTempPath(), "hd2mm-music-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_tempDirectory);
		_service = new BackgroundMusicService(NullLogger<BackgroundMusicService>.Instance, _tempDirectory);
	}

	[TestCleanup]
	public void Cleanup()
	{
		_service.Dispose();
		if (Directory.Exists(_tempDirectory))
			Directory.Delete(_tempDirectory, recursive: true);
	}

	[TestMethod]
	public void EnsureMusicDirectory_CreatesMissingDirectory()
	{
		var missingDirectory = Path.Combine(_tempDirectory, "Music");
		using var service = new BackgroundMusicService(NullLogger<BackgroundMusicService>.Instance, missingDirectory);

		Assert.IsFalse(Directory.Exists(missingDirectory));
		Assert.IsTrue(service.EnsureMusicDirectory());
		Assert.IsTrue(Directory.Exists(missingDirectory));
	}

	[TestMethod]
	public void RefreshPlaylist_FindsSupportedFormatsAndIgnoresOtherFiles()
	{
		File.WriteAllBytes(Path.Combine(_tempDirectory, "b.ogg"), []);
		File.WriteAllBytes(Path.Combine(_tempDirectory, "A.mp3"), []);
		File.WriteAllBytes(Path.Combine(_tempDirectory, "cover.png"), []);

		_service.RefreshPlaylist();

		CollectionAssert.AreEqual(new[] { "A", "b" }, _service.TrackNames.ToArray());
		Assert.AreEqual(0, _service.CurrentTrackIndex);
		Assert.IsFalse(_service.IsPlaying);
	}

	[TestMethod]
	public void RefreshPlaylist_UpdatesListWithoutRestartingService()
	{
		_service.RefreshPlaylist();
		Assert.AreEqual(0, _service.TrackNames.Count);

		File.WriteAllBytes(Path.Combine(_tempDirectory, "new-track.wav"), []);
		_service.RefreshPlaylist();

		Assert.AreEqual(1, _service.TrackNames.Count);
		Assert.AreEqual("new-track", _service.CurrentTrackName);
	}

	[TestMethod]
	public void RefreshPlaylist_RestoresSavedTrackAndFallsBackWhenItWasDeleted()
	{
		var albumDirectory = Path.Combine(_tempDirectory, "Album");
		Directory.CreateDirectory(albumDirectory);
		File.WriteAllBytes(Path.Combine(_tempDirectory, "first.mp3"), []);
		var savedTrack = Path.Combine(albumDirectory, "saved.ogg");
		File.WriteAllBytes(savedTrack, []);

		_service.RefreshPlaylist(Path.Combine("Album", "saved.ogg"));
		Assert.AreEqual("saved", _service.CurrentTrackName);
		Assert.AreEqual(Path.Combine("Album", "saved.ogg"), _service.CurrentTrackRelativePath);

		File.Delete(savedTrack);
		_service.RefreshPlaylist();
		Assert.AreEqual("first", _service.CurrentTrackName);
		Assert.AreEqual("first.mp3", _service.CurrentTrackRelativePath);
	}

	[TestMethod]
	public void PlaybackModes_ApplySequentialLoopAndShuffleRules()
	{
		foreach (var name in new[] { "a.mp3", "b.mp3", "c.mp3" })
			File.WriteAllBytes(Path.Combine(_tempDirectory, name), []);

		_service.RefreshPlaylist("c.mp3");
		_service.SetPlaybackMode(MusicPlaybackMode.Sequential);
		_service.Next();
		Assert.AreEqual("c", _service.CurrentTrackName, "顺序播放到末尾后应停在最后一首");

		_service.SetPlaybackMode(MusicPlaybackMode.Loop);
		_service.Next();
		Assert.AreEqual("a", _service.CurrentTrackName, "循环播放应从末尾回到第一首");

		_service.SetPlaybackMode(MusicPlaybackMode.Shuffle);
		var beforeShuffle = _service.CurrentTrackIndex;
		_service.Next();
		Assert.AreNotEqual(beforeShuffle, _service.CurrentTrackIndex, "多首音乐随机播放时不应连续选中同一首");
	}
}
