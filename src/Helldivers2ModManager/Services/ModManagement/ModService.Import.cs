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
	/// <summary>
	/// 从源目录创建并添加模组。将源目录内容复制到存储目录，
	/// 根据目录结构自动推断清单格式，并使用用户指定的名称和描述。
	/// </summary>
	/// <param name="sourceDir">源目录，包含模组文件</param>
	/// <param name="modName">模组显示名称</param>
	/// <param name="modDescription">模组描述</param>
	/// <param name="customOptions">用户自定义的选项列表（可选，为 null 时自动推断）</param>
	/// <param name="iconPath">模组图标路径（可选，仅文件名部分会写入清单）</param>
	/// <returns>遇到的问题列表</returns>
	public async Task<ModProblem[]> TryAddModFromDirectoryAsync(
		DirectoryInfo sourceDir, string modName, string modDescription,
		List<ModOption>? customOptions = null, string? iconPath = null,
		ManifestVersion targetVersion = ManifestVersion.V1)
	{
		GuardInitialized();

		var problems = new List<ModProblem>();

		_logger.LogInformation("Attempting to add mod from directory \"{}\"", sourceDir.FullName);

		if (!sourceDir.Exists)
		{
			problems.Add(new ModProblem
			{
				Directory = sourceDir,
				Kind = ModProblemKind.InvalidPath,
			});
			return problems.ToArray();
		}

		// 根据源目录结构推断清单
		var manifest = ModManifest.InferFromDirectory(sourceDir, _logger);

		// 使用用户输入的名称和描述覆盖推断值
		var finalName = !string.IsNullOrWhiteSpace(modName) ? modName : manifest.Name;
		var finalDescription = !string.IsNullOrWhiteSpace(modDescription) ? modDescription : manifest.Description;

		// 确定图标路径：优先使用用户指定的图标，否则使用推断的图标
		var finalIconPath = !string.IsNullOrWhiteSpace(iconPath)
			? Path.GetFileName(iconPath)
			: manifest.IconPath;

		// 构建目标目录路径（安全校验：防止路径遍历）
		var safeName = Path.GetFileName(finalName);
		if (string.IsNullOrWhiteSpace(safeName))
		{
			_logger.LogError("Invalid mod name after sanitization: {Name}", finalName);
			problems.Add(new ModProblem
			{
				Directory = sourceDir,
				Kind = ModProblemKind.InvalidPath,
				ExtraData = "Invalid mod name",
			});
			return problems.ToArray();
		}
		
		var modsBasePath = Path.GetFullPath(Path.Combine(_settingsService.StorageDirectory, "Mods"));
		var modDir = new DirectoryInfo(Path.Combine(modsBasePath, safeName));
		
		// 验证最终路径是否在 Mods 目录内
		if (!modDir.FullName.StartsWith(modsBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			&& modDir.FullName != modsBasePath)
		{
			_logger.LogError("Path traversal attempt detected: {Path}", modDir.FullName);
			problems.Add(new ModProblem
			{
				Directory = sourceDir,
				Kind = ModProblemKind.InvalidPath,
				ExtraData = "Path traversal not allowed",
			});
			return problems.ToArray();
		}
		
		if (modDir.Exists)
		{
			_logger.LogWarning("Mod directory already exists: {}", modDir.FullName);
			problems.Add(new ModProblem
			{
				Directory = modDir,
				Kind = ModProblemKind.Duplicate,
			});
			return problems.ToArray();
		}

		// 复制源目录内容到存储目录
		modDir.Parent?.Create();
		await Task.Run(() => sourceDir.CopyTo(modDir.FullName));
		modDir.Refresh();

		// 创建清单文件：按用户选择的格式生成（默认 V1）
		IModManifest finalManifest;
		if (targetVersion == ManifestVersion.V1)
		{
			finalManifest = new V1ModManifest
			{
				Guid = manifest.Guid,
				Name = finalName,
				Description = finalDescription,
				IconPath = finalIconPath,
				Options = customOptions is { Count: > 0 } ? customOptions : null,
			};
		}
		else
		{
			if (customOptions is { Count: > 0 } && customOptions.Any(static o => o.SubOptions is { Count: > 0 }))
				_logger.LogWarning("Legacy manifest does not support sub-options; sub-options will be dropped for \"{}\"", finalName);
			// Legacy 选项名即部署目录名，丢弃空名称选项避免误部署整个模组根目录
			var legacyOptionNames = customOptions?
				.Where(static o => !string.IsNullOrWhiteSpace(o.Name))
				.Select(static o => o.Name.Trim())
				.ToArray();
			if (customOptions is { Count: > 0 } && legacyOptionNames!.Length != customOptions.Count)
				_logger.LogWarning("Dropped {} legacy option(s) with empty names for \"{}\"", customOptions.Count - legacyOptionNames!.Length, finalName);
			finalManifest = new LegacyModManifest
			{
				Guid = manifest.Guid,
				Name = finalName,
				Description = finalDescription,
				IconPath = finalIconPath,
				Options = legacyOptionNames is { Length: > 0 } ? legacyOptionNames : null,
			};
		}

		ModManifest.SaveToFile(finalManifest, modDir);

		_logger.LogInformation("Adding mod");
		var mod = new ModData(modDir, finalManifest);
		_mods.Add(mod);
		_modsByGuid[mod.Manifest.Guid] = mod;
		_modsByPath[mod.Directory.FullName] = mod;
		ModAdded?.Invoke(mod);

		_logger.LogInformation("Mod created successfully: {}", finalName);
		return problems.ToArray();
	}

	/// <summary>
	/// 尝试从压缩包添加模组
	/// </summary>
	/// <param name="file">压缩包文件</param>
	/// <param name="nestedProgress">嵌套压缩包处理进度回调：(当前序号(0-based), 总数, 当前文件名)，仅在检测到嵌套压缩包时调用</param>
	public async Task<ModProblem[]> TryAddModFromArchiveAsync(FileInfo file, Action<int, int, string>? nestedProgress = null)
	{
		GuardInitialized();

		var problems = new List<ModProblem>();

		_logger.LogInformation("Attempting to add mod from \"{}\"", file.Name);

		// 使用文件名 + 短GUID 作为临时目录名，避免嵌套压缩包与外层同名时发生路径覆盖
		// 例如：外层压缩包和嵌套压缩包都叫 "mod.zip" 时，两级的临时目录名会不同
		var tmpDirName = $"{file.Name[..^file.Extension.Length]}_{Guid.NewGuid():N}"[..^24];
		var tmpDir = new DirectoryInfo(Path.Combine(_settingsService.TempDirectory, tmpDirName));
		_logger.LogInformation("Creating clean temporary directory \"{}\"", tmpDir.FullName);
		if (tmpDir.Exists)
			tmpDir.Delete(true);
		tmpDir.Create();

		_logger.LogInformation("Extracting archive using SharpSevenZip");
		try
		{
			await Task.Run(() =>
			{
				// SharpSevenZip 通过原生 7z.dll 支持所有压缩格式（7z/zip/rar/tar 等）
				// 自动通过文件签名检测归档格式，无需手动区分扩展名
				// 原生 7z.dll 支持大字典 LZMA，解决 SharpCompress 纯托管实现的兼容性问题
				using var extractor = new SharpSevenZipExtractor(file.FullName);
				extractor.ExtractArchive(tmpDir.FullName);
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Failed to extract archive \"{}\"", file.Name);
			tmpDir.Delete(true);
			problems.Add(new ModProblem
			{
				Directory = tmpDir,
				Kind = ModProblemKind.CantReadArchive,
				ExtraData = ex.Message,
			});
			return problems.ToArray();
		}

		_logger.LogDebug("Checking for unnecessary root folder in extracted archive");
		var rootFolders = tmpDir.GetDirectories();
		var rootFiles = tmpDir.GetFiles();
		
		if (rootFolders.Length == 1 && rootFiles.Length == 0)
		{
			var rootFolder = rootFolders[0];
			_logger.LogInformation("Detected root folder \"{}\", flattening structure", rootFolder.Name);
			
			await MoveDirectoryContentsAsync(rootFolder, tmpDir);
			rootFolder.Delete(true);
			_logger.LogDebug("Root folder flattened successfully");
		}

		// 检测嵌套压缩包场景：一级压缩包中未直接包含模组清单文件，但包含其他压缩包（文件夹嵌套结构）
		// 此时应以嵌套压缩包作为主要导入对象，支持批量导入所有符合条件的嵌套压缩包
		var manifestFile = new FileInfo(Path.Combine(tmpDir.FullName, "manifest.json"));
		var nestedArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{ ".zip", ".7z", ".rar", ".tar" };

		if (!manifestFile.Exists)
		{
			// 递归搜索所有嵌套压缩包（支持文件夹嵌套结构中的压缩包）
			var nestedArchives = tmpDir.GetFiles("*", System.IO.SearchOption.AllDirectories)
				.Where(f => nestedArchiveExtensions.Contains(f.Extension))
				.ToArray();

			if (nestedArchives.Length > 0)
			{
				_logger.LogInformation("一级压缩包中未发现 manifest.json，但检测到 {Count} 个嵌套压缩包，将以嵌套压缩包作为导入对象进行批量导入", nestedArchives.Length);

				var allNestedProblems = new List<ModProblem>();

				for (int i = 0; i < nestedArchives.Length; i++)
				{
					var nestedArchive = nestedArchives[i];
					
					// 向调用方汇报嵌套导入进度（当前序号, 总数, 当前文件名）
					nestedProgress?.Invoke(i, nestedArchives.Length, nestedArchive.Name);

					_logger.LogInformation("开始处理嵌套压缩包 ({Current}/{Total}): {Name}", i + 1, nestedArchives.Length, nestedArchive.Name);
					try
					{
						// 递归处理嵌套压缩包，传递同一进度回调以支持多层嵌套的进度上报
						var nestedProblems = await TryAddModFromArchiveAsync(nestedArchive, nestedProgress);
						allNestedProblems.AddRange(nestedProblems);
					}
					catch (Exception ex)
					{
						_logger.LogError(ex, "处理嵌套压缩包失败: {Name}", nestedArchive.Name);
						allNestedProblems.Add(new ModProblem
						{
							Directory = tmpDir,
							Kind = ModProblemKind.CantReadArchive,
							ExtraData = $"{nestedArchive.Name}: {ex.Message}",
						});
					}
				}

				// 清理包装压缩包的临时目录（嵌套压缩包已被递归提取到各自临时目录并完成导入）
				tmpDir.Delete(true);
				_logger.LogInformation("嵌套压缩包批量导入完成，共处理 {Count} 个", nestedArchives.Length);

				return allNestedProblems.ToArray();
			}
		}

		IModManifest manifest;
		if (manifestFile.Exists)
		{
			manifest = ModManifest.DeserializeFromFile(manifestFile);

			if (!CheckPaths(manifest, problems, tmpDir, manifestFile))
			{
				tmpDir.Delete(true);
				return problems.ToArray();
			}

			// 自动清理指向不存在文件的图片路径（无效图标/选项图），
			// 避免无效图片路径在每次启动时反复报告问题。
			var sanitized = SanitizeManifestImagePaths(manifest, tmpDir, _logger);
			if (!ReferenceEquals(sanitized, manifest))
			{
				manifest = sanitized;
				ModManifest.SaveToFile(manifest, tmpDir);
				_logger.LogInformation("Sanitized invalid image paths in manifest \"{}\"", manifestFile.Name);
			}
		}
		else
		{
			problems.Add(new ModProblem
			{
				Directory = tmpDir,
				Kind = ModProblemKind.NoManifestFound,
			});
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

		_logger.LogInformation("Moving mod to storage");
		
		// 安全校验：防止路径遍历
		var safeModName = Path.GetFileName(manifest.Name);
		if (string.IsNullOrWhiteSpace(safeModName))
		{
			_logger.LogError("Invalid mod name after sanitization: {Name}", manifest.Name);
			tmpDir.Delete(true);
			problems.Add(new ModProblem
			{
				Directory = tmpDir,
				Kind = ModProblemKind.InvalidPath,
				ExtraData = "Invalid mod name",
			});
			return problems.ToArray();
		}
		
		var modsBasePath = Path.GetFullPath(Path.Combine(_settingsService.StorageDirectory, "Mods"));
		var modDir = new DirectoryInfo(Path.Combine(modsBasePath, safeModName));
		
		// 验证最终路径是否在 Mods 目录内
		if (!modDir.FullName.StartsWith(modsBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
			&& modDir.FullName != modsBasePath)
		{
			_logger.LogError("Path traversal attempt detected: {Path}", modDir.FullName);
			tmpDir.Delete(true);
			problems.Add(new ModProblem
			{
				Directory = tmpDir,
				Kind = ModProblemKind.InvalidPath,
				ExtraData = "Path traversal not allowed",
			});
			return problems.ToArray();
		}
		
		if (modDir.Exists)
		{
			_logger.LogInformation("Mod directory already exists, comparing files");
			
			var existingMod = _modsByPath.TryGetValue(modDir.FullName, out var existing) ? existing : null;
			if (existingMod != null && await AreDirectoriesEqualAsync(tmpDir, modDir))
			{
				_logger.LogError("Mod files are identical, skipping");
				tmpDir.Delete(true);
				problems.Add(new ModProblem
				{
					Directory = modDir,
					Kind = ModProblemKind.Duplicate,
				});
				return problems.ToArray();
			}
			
			_logger.LogInformation("Mod files are different, updating");
			var recycleOption = _settingsService.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
			await Task.Run(() => FileSystem.DeleteDirectory(modDir.FullName, UIOption.OnlyErrorDialogs, recycleOption));
			
			if (existingMod != null)
			{
				_mods.Remove(existingMod);
				_modsByGuid.Remove(existingMod.Manifest.Guid);
				_modsByPath.Remove(existingMod.Directory.FullName);
			}
		}
		modDir.Parent?.Create();
		await Task.Run(() => tmpDir.CopyTo(modDir.FullName));
		modDir.Refresh();

		_logger.LogInformation("Adding mod");
		var mod = new ModData(modDir, manifest);
		_mods.Add(mod);
		_modsByGuid[mod.Manifest.Guid] = mod;
		_modsByPath[mod.Directory.FullName] = mod;
		ModAdded?.Invoke(mod);

		// 后台异步计算并存储新模组的文件哈希值（fire-and-forget，不阻塞导入流程）
		_modHashService.ComputeAndStoreForModAsync(mod);

		tmpDir.Delete(true);
		return problems.ToArray();
	}
}
