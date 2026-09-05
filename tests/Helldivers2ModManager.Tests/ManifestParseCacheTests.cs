using System.IO;
using System.Text.Json;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Tests;

[TestClass]
public sealed class ManifestParseCacheTests
{
	private string _tempDir = null!;
	private string _modDir = null!;

	[TestInitialize]
	public void Setup()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), $"hd2mm-cache-test-{Guid.NewGuid():N}");
		_modDir = Path.Combine(_tempDir, "Mods", "test-mod");
		Directory.CreateDirectory(_modDir);
	}

	[TestCleanup]
	public void Cleanup()
	{
		try
		{
			if (Directory.Exists(_tempDir))
				Directory.Delete(_tempDir, recursive: true);
		}
		catch { /* 清理失败不影响测试结果 */ }
	}

	[TestMethod]
	public void ComputeFingerprint_IsStableForUnchangedDirectory()
	{
		File.WriteAllText(Path.Combine(_modDir, "manifest.json"), "{}");
		Directory.CreateDirectory(Path.Combine(_modDir, "opt1"));
		File.WriteAllText(Path.Combine(_modDir, "opt1", "a.patch_0"), "abc");

		var fp1 = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));
		var fp2 = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));

		Assert.AreEqual(fp1, fp2);
		Assert.AreEqual(64, fp1.Length);
	}

	[TestMethod]
	public void ComputeFingerprint_ChangesWhenFileContentChanges()
	{
		var manifestPath = Path.Combine(_modDir, "manifest.json");
		File.WriteAllText(manifestPath, "{}");
		var before = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));

		File.WriteAllText(manifestPath, "{ \"Name\": \"changed\" }");
		var after = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));

		Assert.AreNotEqual(before, after);
	}

	[TestMethod]
	public void ComputeFingerprint_ChangesWhenFileAddedOrRemoved()
	{
		var before = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));

		File.WriteAllText(Path.Combine(_modDir, "extra.bin"), "x");
		var afterAdd = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));
		Assert.AreNotEqual(before, afterAdd);

		File.Delete(Path.Combine(_modDir, "extra.bin"));
		var afterRemove = ManifestParseCache.ComputeFingerprint(new DirectoryInfo(_modDir));
		Assert.AreNotEqual(afterAdd, afterRemove);
	}

	[TestMethod]
	public void SaveThenLoad_RoundTripsEntries()
	{
		var storage = _tempDir;
		var cache = new ManifestParseCache(NullLogger.Instance);
		cache.Store("mod-a", new CachedModEntry
		{
			Fingerprint = "fp-a",
			ManifestJson = "{\"Guid\":\"00000000-0000-0000-0000-000000000001\"}",
			Succeeded = true,
			Problems =
			[
				new CachedModProblem { Kind = 2, ExtraData = "opt1" },
			],
		});
		cache.Store("mod-b", new CachedModEntry
		{
			Fingerprint = "fp-b",
			ManifestJson = null,
			Succeeded = false,
			FailureKind = (int)Models.ModProblemKind.CantParseManifest,
		});
		cache.Save(storage);

		var reloaded = ManifestParseCache.Load(storage, NullLogger.Instance);
		Assert.IsTrue(reloaded.TryGet("mod-a", "fp-a", out var entryA));
		Assert.AreEqual("{\"Guid\":\"00000000-0000-0000-0000-000000000001\"}", entryA!.ManifestJson);
		Assert.IsTrue(entryA.Succeeded);
		Assert.AreEqual(1, entryA.Problems.Count);
		Assert.AreEqual(2, entryA.Problems[0].Kind);
		Assert.AreEqual("opt1", entryA.Problems[0].ExtraData);

		Assert.IsTrue(reloaded.TryGet("mod-b", "fp-b", out var entryB));
		Assert.IsNull(entryB!.ManifestJson);
		Assert.AreEqual((int)Models.ModProblemKind.CantParseManifest, entryB.FailureKind);
	}

	[TestMethod]
	public void TryGet_RejectsChangedFingerprint()
	{
		var cache = new ManifestParseCache(NullLogger.Instance);
		cache.Store("mod-a", new CachedModEntry { Fingerprint = "fp-1" });

		Assert.IsFalse(cache.TryGet("mod-a", "fp-2", out _));
		Assert.IsFalse(cache.TryGet("mod-missing", "fp-1", out _));
	}

	[TestMethod]
	public void Load_WithCorruptFile_ReturnsEmptyCache()
	{
		var cacheDir = Path.Combine(_tempDir, "cache");
		Directory.CreateDirectory(cacheDir);
		File.WriteAllText(Path.Combine(cacheDir, "manifest_parse_cache.json"), "{ not valid json !!!");

		var cache = ManifestParseCache.Load(_tempDir, NullLogger.Instance);

		Assert.IsFalse(cache.TryGet("anything", "fp", out _));
	}

	[TestMethod]
	public void Load_WithVersionMismatch_ReturnsEmptyCache()
	{
		var cacheDir = Path.Combine(_tempDir, "cache");
		Directory.CreateDirectory(cacheDir);
		var path = Path.Combine(cacheDir, "manifest_parse_cache.json");
		File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 999, Entries = new Dictionary<string, object>() }));

		var cache = ManifestParseCache.Load(_tempDir, NullLogger.Instance);

		Assert.IsFalse(cache.TryGet("anything", "fp", out _));
	}

	[TestMethod]
	public void Load_WithMissingFile_ReturnsEmptyCache()
	{
		var cache = ManifestParseCache.Load(_tempDir, NullLogger.Instance);

		Assert.IsFalse(cache.TryGet("anything", "fp", out _));
	}

	[TestMethod]
	public void Save_IsAtomicAndReplacesExistingFile()
	{
		var storage = _tempDir;
		var cache = new ManifestParseCache(NullLogger.Instance);
		cache.Store("mod-a", new CachedModEntry { Fingerprint = "fp-1" });
		cache.Save(storage);

		var cache2 = new ManifestParseCache(NullLogger.Instance);
		cache2.Store("mod-a", new CachedModEntry { Fingerprint = "fp-2" });
		cache2.Save(storage);

		var reloaded = ManifestParseCache.Load(storage, NullLogger.Instance);
		Assert.IsTrue(reloaded.TryGet("mod-a", "fp-2", out _));
		Assert.IsFalse(reloaded.TryGet("mod-a", "fp-1", out _));
	}

	[TestMethod]
	public void Remove_DeletesEntry()
	{
		var cache = new ManifestParseCache(NullLogger.Instance);
		cache.Store("mod-a", new CachedModEntry { Fingerprint = "fp-1" });
		cache.Remove("mod-a");

		Assert.IsFalse(cache.TryGet("mod-a", "fp-1", out _));
	}
}
