using System.IO;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Services;

internal static class FileHashUtils
{
	private static LocalizationService? _localizationService;

	internal static void Init(LocalizationService localizationService)
	{
		_localizationService = localizationService;
	}

	public static async Task<string> ComputeFileHashAsync(FileInfo file)
	{
		var service = new FileHashService(NullFileHashRepository.Instance);
		return await service.ComputeFileHashAsync(file);
	}

	public static async Task<Dictionary<string, string>> ComputeDirectoryHashesAsync(
		DirectoryInfo directory,
		IProgress<(int checkedCount, int totalCount, string currentFile)>? progress = null,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var service = new FileHashService(NullFileHashRepository.Instance);
			var hashes = await service.ComputeDirectoryHashesAsync(
				directory,
				progress is null ? null : new DirectoryHashProgressAdapter(progress),
				cancellationToken);
			return new(hashes, StringComparer.OrdinalIgnoreCase);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw CreateHashError(ex, directory.Name, ex.Message);
		}
	}

	public static async Task<Dictionary<string, string>> ComputeDirectoryHashesWithCacheAsync(
		DirectoryInfo directory,
		Guid modGuid,
		IFileHashRepository repo,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress = null,
		CancellationToken cancellationToken = default) =>
		await ComputeCachedAsync(directory, modGuid, repo, true, progress, cancellationToken);

	public static async Task<Dictionary<string, string>> ComputeDirectoryHashesReadCacheAsync(
		DirectoryInfo directory,
		Guid modGuid,
		IFileHashRepository repo,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress = null,
		CancellationToken cancellationToken = default) =>
		await ComputeCachedAsync(directory, modGuid, repo, false, progress, cancellationToken);

	public static HashCompareResult CompareHashes(
		Dictionary<string, string> currentHashes,
		Dictionary<string, string> newHashes)
	{
		var comparison = FileHashService.CompareHashes(currentHashes, newHashes);
		return new HashCompareResult
		{
			ChangedFiles = [.. comparison.ChangedFiles],
			DeletedFiles = [.. comparison.DeletedFiles],
			UnchangedCount = comparison.UnchangedCount,
			TotalNewFiles = comparison.TotalNewFiles,
			TotalCurrentFiles = comparison.TotalCurrentFiles,
		};
	}

	private static async Task<Dictionary<string, string>> ComputeCachedAsync(
		DirectoryInfo directory,
		Guid modGuid,
		IFileHashRepository repo,
		bool saveToDb,
		IProgress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>? progress,
		CancellationToken cancellationToken)
	{
		try
		{
			var service = new FileHashService(repo);
			var hashes = await service.ComputeDirectoryHashesWithCacheAsync(
				directory,
				modGuid,
				saveToDb,
				progress is null ? null : new CachedHashProgressAdapter(progress),
				cancellationToken);
			return new(hashes, StringComparer.OrdinalIgnoreCase);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw CreateHashError(ex, directory.Name, ex.Message);
		}
	}

	private static IOException CreateHashError(Exception exception, string path, string message) =>
		new(_localizationService?["FileHashUtils.HashError"]
			.Replace("{path}", path)
			.Replace("{message}", message) ?? $"Unable to hash \"{path}\": {message}", exception);

	private sealed class DirectoryHashProgressAdapter(
		IProgress<(int CheckedCount, int TotalCount, string CurrentFile)> progress) : IProgress<DirectoryHashProgress>
	{
		public void Report(DirectoryHashProgress value) =>
			progress.Report((value.CheckedCount, value.TotalCount, value.CurrentFile));
	}

	private sealed class CachedHashProgressAdapter(
		IProgress<(int CheckedCount, int TotalCount, string CurrentFile, int CacheHits)> progress)
		: IProgress<CachedDirectoryHashProgress>
	{
		public void Report(CachedDirectoryHashProgress value) =>
			progress.Report((value.CheckedCount, value.TotalCount, value.CurrentFile, value.CacheHits));
	}
}

internal sealed class HashCompareResult
{
	public required List<string> ChangedFiles { get; init; }

	public required List<string> DeletedFiles { get; init; }

	public int UnchangedCount { get; init; }

	public int TotalNewFiles { get; init; }

	public int TotalCurrentFiles { get; init; }

	public bool HasChanges => ChangedFiles.Count > 0 || DeletedFiles.Count > 0;
}

internal sealed class NullFileHashRepository : IFileHashRepository
{
	public static NullFileHashRepository Instance { get; } = new();

	public Task<IReadOnlyList<FileHashRecord>> LoadForModAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("Single-file hashing does not use the hash cache.");

	public Task ReplaceForModAsync(Guid modGuid, IReadOnlyList<FileHashRecord> records, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("Single-file hashing does not use the hash cache.");

	public Task DeleteForModAsync(Guid modGuid, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("Single-file hashing does not use the hash cache.");
}
