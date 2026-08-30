using Helldivers2ModManager.Exceptions;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using SharpSevenZip;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Services;

/// <summary>ModService 按流水线拆分的 partial 文件（导入/更新/部署），成员仍属于 ModService。</summary>
internal sealed partial class ModService
{

	public async Task UpdateModFromArchiveAsync(ModData mod, FileInfo archive, IProgress<UpdateProgressInfo>? progress = null)
	{
		GuardInitialized();

		_logger.LogInformation("Attempting to update mod \"{}\" from archive \"{}\" with hash-based incremental update", mod.Manifest.Name, archive.Name);

		// 阶段1: 计算当前mod目录中所有文件的SHA-256哈希值（优先使用数据库缓存）
		progress?.Report(new UpdateProgressInfo
		{
			Phase = UpdatePhase.HashingCurrent,
			Message = _localizationService["ModService.CalculatingHashes"]
		});
		_logger.LogDebug("Computing SHA-256 hashes for current mod files (with cache)");

		var loc = _localizationService;
		var hashingProgress = new Progress<(int checkedCount, int totalCount, string currentFile, int cacheHits)>(p =>
		{
			progress?.Report(new UpdateProgressInfo
			{
				Phase = UpdatePhase.HashingCurrent,
				CurrentFile = p.currentFile,
				ProcessedCount = p.checkedCount,
				TotalCount = p.totalCount,
				CacheHits = p.cacheHits,
				Message = loc["ModService.CalculatingHashesProgress"]
					.Replace("{done}", p.checkedCount.ToString())
					.Replace("{total}", p.totalCount.ToString())
			});
		});

		Dictionary<string, string> currentHashes;
		try
		{
			currentHashes = await FileHashUtils.ComputeDirectoryHashesReadCacheAsync(
				mod.Directory,
				mod.Manifest.Guid,
				_fileHashRepository,
				_settingsService.StorageDirectory,
				hashingProgress);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to compute hashes for current mod \"{}\"", mod.Manifest.Name);
			throw new IOException(_localizationService["ModService.HashError"].Replace("{message}", ex.Message), ex);
		}
		_logger.LogInformation("Computed hashes for {Count} current files", currentHashes.Count);

		// 解压到临时目录
		var tmpDir = new DirectoryInfo(Path.Combine(_settingsService.TempDirectory, $"update_{mod.Manifest.Guid:N}"));
		_logger.LogInformation("Creating clean temporary directory \"{}\"", tmpDir.FullName);
		if (tmpDir.Exists)
			tmpDir.Delete(true);
		tmpDir.Create();

		try
		{
			await Task.Run(() =>
			{
				using var extractor = new SharpSevenZipExtractor(archive.FullName);
				extractor.ExtractArchive(tmpDir.FullName);
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to extract archive \"{}\"", archive.Name);
			tmpDir.Delete(true);
			throw;
		}

		// 展平根文件夹
		var rootFolders = tmpDir.GetDirectories();
		var rootFiles = tmpDir.GetFiles();
		if (rootFolders.Length == 1 && rootFiles.Length == 0)
		{
			var rootFolder = rootFolders[0];
			_logger.LogInformation("Detected root folder \"{}\", flattening structure", rootFolder.Name);
			await MoveDirectoryContentsAsync(rootFolder, tmpDir);
			rootFolder.Delete(true);
		}

		// 查找或推断清单（必须在阶段2哈希计算之前，确保manifest.json参与比较）
		var manifestFile = new FileInfo(Path.Combine(tmpDir.FullName, "manifest.json"));
		IModManifest manifest;
		if (manifestFile.Exists)
		{
			manifest = ModManifest.DeserializeFromFile(manifestFile);
		}
		else
		{
			_logger.LogInformation("No manifest.json found in archive, inferring from directory structure");
			manifest = ModManifest.InferFromDirectory(tmpDir);
			using var stream = manifestFile.Open(FileMode.CreateNew, FileAccess.Write, FileShare.Read);
			using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
			{
				IndentCharacter = '\t',
				Indented = true,
				IndentSize = 1,
			});
			manifest.Serialize(writer);
			await writer.DisposeAsync();
		}

		// 保留原有名称、描述、图标路径和 Guid，避免被压缩包中的清单覆盖
		// 注意: 必须保留原 Guid，否则旧文件哈希记录将无法被清理（哈希表以 Guid 为键）
		manifest = manifest.Version switch
		{
			ManifestVersion.Legacy => new LegacyModManifest
			{
				Guid = mod.Manifest.Guid,
				Name = mod.Manifest.Name,
				Description = mod.Manifest.Description,
				IconPath = mod.Manifest.IconPath,
				Options = ((LegacyModManifest)manifest).Options,
			},
			ManifestVersion.V1 => new V1ModManifest
			{
				Guid = mod.Manifest.Guid,
				Name = mod.Manifest.Name,
				Description = mod.Manifest.Description,
				IconPath = mod.Manifest.IconPath,
				Options = ((V1ModManifest)manifest).Options,
				NexusData = ((V1ModManifest)manifest).NexusData,
			},
			_ => throw new NotSupportedException($"Unsupported manifest version: {manifest.Version}")
		};

		// 保存旧的状态（启用状态、分组、标签等）
		var oldState = mod.ToEnabledData();

		// 阶段2: 计算新版本文件的SHA-256哈希值（manifest.json 此时已存在）
		progress?.Report(new UpdateProgressInfo
		{
			Phase = UpdatePhase.HashingNew,
			Message = _localizationService["ModService.CalculatingNewHashes"]
		});
		_logger.LogDebug("Computing SHA-256 hashes for new version files");

		var newHashingProgress = new Progress<(int checkedCount, int totalCount, string currentFile)>(p =>
		{
			progress?.Report(new UpdateProgressInfo
			{
				Phase = UpdatePhase.HashingNew,
				CurrentFile = p.currentFile,
				ProcessedCount = p.checkedCount,
				TotalCount = p.totalCount,
				Message = loc["ModService.CalculatingNewHashesProgress"]
					.Replace("{done}", p.checkedCount.ToString())
					.Replace("{total}", p.totalCount.ToString())
			});
		});

		Dictionary<string, string> newHashes;
		try
		{
			newHashes = await FileHashUtils.ComputeDirectoryHashesAsync(tmpDir, newHashingProgress);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to compute hashes for new version");
			tmpDir.Delete(true);
			throw new IOException(_localizationService["ModService.NewHashError"].Replace("{message}", ex.Message), ex);
		}
		_logger.LogInformation("Computed hashes for {Count} new files", newHashes.Count);

		// 阶段3: 比对两组哈希值，识别变更/新增/删除的文件
		progress?.Report(new UpdateProgressInfo
		{
			Phase = UpdatePhase.Comparing,
			Message = _localizationService["ModService.ComparingFiles"]
		});
		var compareResult = FileHashUtils.CompareHashes(currentHashes, newHashes);

		_logger.LogInformation(
			"Hash comparison: {Changed} changed/new, {Deleted} deleted, {Unchanged} unchanged (total: current={CurCount}, new={NewCount})",
			compareResult.ChangedFiles.Count,
			compareResult.DeletedFiles.Count,
			compareResult.UnchangedCount,
			compareResult.TotalCurrentFiles,
			compareResult.TotalNewFiles);

		if (!compareResult.HasChanges)
		{
			// 哈希完全一致，无需更新文件，但仍然更新清单（清单内容可能变化）
			_logger.LogInformation("All files identical by hash, no file-level update needed");
			tmpDir.Delete(true);

			mod.Manifest = manifest;
			ModManifest.SaveToFile(manifest, mod.Directory);
			mod.ApplyData(oldState);

			progress?.Report(new UpdateProgressInfo
			{
				Phase = UpdatePhase.Completed,
				IsCompleted = true,
				Message = _localizationService["ModService.AllFilesUpToDate"]
			});
			_logger.LogInformation("Mod \"{}\" manifest updated (no file changes needed)", mod.Manifest.Name);
			return;
		}

		// 阶段4: 执行增量更新
		// 4.1 删除新版本中不存在的旧文件（基于目录枚举直接对比，避免路径规范化差异导致遗漏）
		// 先构建新版本文件的路径集合（统一使用'/'分隔符）
		var newFileSet = new HashSet<string>(newHashes.Keys, StringComparer.OrdinalIgnoreCase);

		// 枚举当前mod目录中所有文件，删除不在新版本中的文件
		var currentFiles = mod.Directory.GetFiles("*", System.IO.SearchOption.AllDirectories);
		var deletedCount = 0;
		foreach (var file in currentFiles)
		{
			var relativePath = file.FullName
				.Substring(mod.Directory.FullName.Length)
				.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				.Replace('\\', '/');

			if (!newFileSet.Contains(relativePath))
			{
				try
				{
					file.Delete();
					deletedCount++;
					_logger.LogDebug("Deleted obsolete file \"{Path}\"", relativePath);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Failed to delete obsolete file \"{Path}\"", relativePath);
				}
			}
		}

		if (deletedCount > 0)
		{
			_logger.LogInformation("Deleted {Count} obsolete files from mod \"{Name}\"", deletedCount, mod.Manifest.Name);
		}

		// 清理因文件删除而产生的空目录
		try
		{
			CleanEmptyDirectories(mod.Directory);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean empty directories for mod \"{Name}\"", mod.Manifest.Name);
		}

		// 4.2 复制变更/新增的文件（仅更新哈希不同的文件，实现增量更新）
		var filesToUpdate = compareResult.ChangedFiles;
		_logger.LogInformation("Updating {Count} changed/new files incrementally", filesToUpdate.Count);

		// 有界并行拷贝：文件多时显著缩短更新耗时（串行逐文件 await 会按文件数线性累加 IO 延迟）。
		// Directory.CreateDirectory 幂等，并行创建目标目录安全；进度按完成数单调上报。
		var updatedCount = 0;
		await Parallel.ForEachAsync(
			filesToUpdate,
			new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) },
			(relativePath, _) =>
			{
				var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
				var sourcePath = Path.Combine(tmpDir.FullName, normalizedPath);
				var destPath = Path.Combine(mod.Directory.FullName, normalizedPath);

				// 确保目标目录存在
				var destDir = Path.GetDirectoryName(destPath);
				if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
					Directory.CreateDirectory(destDir);

				try
				{
					// body 已在线程池线程执行（Parallel.ForEachAsync），无需再包 Task.Run
					File.Copy(sourcePath, destPath, true);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Failed to copy file \"{Path}\"", relativePath);
					// 并行 worker 可能同时失败并竞争删除同一目录，忽略二次删除的竞争异常
					try
					{
						tmpDir.Delete(true);
					}
					catch
					{
					}
					throw new IOException(_localizationService["ModService.FileUpdateError"].Replace("{path}", relativePath).Replace("{message}", ex.Message), ex);
				}

				// 报告更新进度
				var done = Interlocked.Increment(ref updatedCount);
				progress?.Report(new UpdateProgressInfo
				{
					Phase = UpdatePhase.Updating,
					CurrentFile = relativePath,
					ProcessedCount = done,
					TotalCount = filesToUpdate.Count,
					NeedUpdateCount = filesToUpdate.Count,
					Message = _localizationService["ModService.UpdatingProgress"]
						.Replace("{current}", done.ToString())
						.Replace("{total}", filesToUpdate.Count.ToString())
				});
				return ValueTask.CompletedTask;
			});

		// 清理临时目录
		tmpDir.Delete(true);

		// 更新清单并保存
		mod.Manifest = manifest;
		ModManifest.SaveToFile(manifest, mod.Directory);

		// 恢复状态，并根据新清单重新适配选项数组
		mod.ApplyData(oldState);

		// 报告完成
		progress?.Report(new UpdateProgressInfo
		{
			Phase = UpdatePhase.Completed,
			IsCompleted = true,
			UnchangedCount = compareResult.UnchangedCount,
			NeedUpdateCount = filesToUpdate.Count,
			DeletedCount = deletedCount,
			Message = _localizationService["ModService.UpdateComplete"]
				.Replace("{updated}", filesToUpdate.Count.ToString())
				.Replace("{skipped}", compareResult.UnchangedCount.ToString())
				.Replace("{failed}", deletedCount.ToString())
		});

		_logger.LogInformation(
			"Mod \"{}\" updated successfully: {Updated} updated, {Unchanged} unchanged, {Deleted} deleted",
			mod.Manifest.Name, filesToUpdate.Count, compareResult.UnchangedCount, deletedCount);

		// 更新完成后重新计算并替换该模组的文件哈希记录（删除旧缓存，存储新哈希值）
		await _modHashService.RecomputeForUpdatedModAsync(mod);
	}
}
