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
	public async Task<ModProblem[]> TryAddModFromArchiveAsync(
		FileInfo file,
		Action<int, int, string>? nestedProgress = null,
		Func<Task<string?>>? passwordProvider = null,
		string? password = null)
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
		Exception? extractionError = await ExtractArchiveAsync(file, tmpDir, password);
		if (extractionError is not null && password is null && passwordProvider is not null && IsWrongPassword(extractionError))
		{
			// 清理第一次无密码尝试留下的部分文件后，再等待 UI 提供密码。
			await TryDeleteTemporaryDirectory(tmpDir);
			tmpDir.Create();
			password = await passwordProvider();
			if (!string.IsNullOrEmpty(password))
				extractionError = await ExtractArchiveAsync(file, tmpDir, password);
			else
				extractionError = new OperationCanceledException("Archive password input was canceled.");
		}

		if (extractionError is not null)
		{
			_logger.LogError(extractionError, "Failed to extract archive \"{}\"", file.Name);
			await TryDeleteTemporaryDirectory(tmpDir);
			problems.Add(new ModProblem
			{
				Directory = tmpDir,
				Kind = ModProblemKind.CantReadArchive,
				ExtraData = extractionError.Message,
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
						var nestedProblems = await TryAddModFromArchiveAsync(nestedArchive, nestedProgress, passwordProvider, password);
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

				// 清理包装压缩包的临时目录（嵌套压缩包已被递归提取到各自临时目录并完成导入）。
				// 走 TryDeleteTemporaryDirectory 而不是直接 Delete(true)，让 AV 实时扫描未释放时
				// 也能完成清理（参见 line ~189 处的说明）。
				await TryDeleteTemporaryDirectory(tmpDir);
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

	/// <summary>
	/// 调 SharpSevenZip 解压单个压缩包到目标目录。
	///
	/// 关键陷阱（2026-09-11 实测）：
	/// SharpSevenZip 写出文件后到我们读取之间存在一个时间窗口，期间 Windows Defender 等
	/// 反病毒软件可能以独占/读共享锁（minifilter 内核层行为表现为 0x80070020 sharing violation）
	/// 持有刚创建的文件。这在嵌套压缩包场景特别明显：外层刚把 inner 写到 tmpDir，
	/// 紧接着的递归解压就撞上 sharing violation。SharpSevenZip 的 `ExtractArchive` 是同步的，
	/// Dispose 也已经把所有 native 句柄释放——所以根本原因不是「外层还没写完 inner 就在读」，
	/// 而是 AV 占用刚写入的文件。
	///
	/// 修复：仅对 sharing/lock violation 做有限次线性退避重试；**其余异常必须原样返回给调用方**。
	/// 调用方（`TryAddModFromArchiveAsync`）用返回的异常判定 `IsWrongPassword` 来弹出密码输入框——
	/// 一旦这里把异常抛出去而不是返回，密码流程会被整个跳过，错误直接穿透到 UI 顶层弹窗。
	/// 因此 catch-all 兜底不可省（2026-09-11 曾因漏掉它导致密码错误变成硬崩溃式弹窗）。
	/// </summary>
	private async Task<Exception?> ExtractArchiveAsync(FileInfo file, DirectoryInfo destination, string? password)
	{
		const int maxAttempts = 6;

		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				await Task.Run(() =>
				{
					using var extractor = string.IsNullOrEmpty(password)
						? new SharpSevenZipExtractor(file.FullName)
						: new SharpSevenZipExtractor(file.FullName, password);
					extractor.ExtractArchive(destination.FullName);
				});
				return null;
			}
			catch (IOException ex) when (IsTransientFileLock(ex) && attempt < maxAttempts)
			{
				var delayMs = 200 * attempt;
				_logger.LogWarning(
					"Extraction of \"{File}\" hit sharing/lock violation on attempt {Attempt}/{Max}: {Message}. " +
					"This is typically caused by antivirus (e.g. Windows Defender) still scanning the file. " +
					"Retrying after {Delay} ms.",
					file.Name, attempt, maxAttempts, ex.Message, delayMs);
				await Task.Delay(delayMs);
			}
			catch (Exception ex)
			{
				// 密码错误、CRC、格式不支持、文件不存在，以及重试耗尽的 sharing violation：
				// 全部原样返回，由调用方决定是弹密码框还是记为 CantReadArchive 问题。
				return ex;
			}
		}

		return null; // 不可达：每次迭代要么 return null，要么 return ex。
	}

	/// <summary>
	/// 判断 IOException 是否为可重试的临时文件锁冲突。
	/// 0x80070020 = ERROR_SHARING_VIOLATION；0x80070021 = ERROR_LOCK_VIOLATION。
	/// </summary>
	private static bool IsTransientFileLock(IOException ex)
	{
		const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
		const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);
		return ex.HResult == ERROR_SHARING_VIOLATION
			|| ex.HResult == ERROR_LOCK_VIOLATION;
	}

	private static bool IsWrongPassword(Exception exception)
	{
		for (var current = exception; current is not null; current = current.InnerException)
		{
			if (current.Message.Contains("Wrong password", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	private async Task TryDeleteTemporaryDirectory(DirectoryInfo directory)
	{
		// 同样按 AV 实时扫描窗口加有限次重试：刚 SharpSevenZip 写出的文件 AV 还在扫描，
		// 立刻 directory.Delete(true) 会因为目录里的文件被独占而抛 sharing violation。
		// 累计延迟最多约 0.6s，绝大多数 AV 扫描能在窗口内结束。
		const int maxAttempts = 4;
		Exception? lastError = null;

		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				if (directory.Exists)
					directory.Delete(true);
				return;
			}
			catch (IOException ex) when (IsTransientFileLock(ex))
			{
				lastError = ex;
				await Task.Delay(150 * attempt);
			}
			catch (Exception ex)
			{
				lastError = ex;
				break;
			}
		}

		if (lastError is not null)
		{
			_logger.LogWarning(lastError, "Failed to clean temporary archive directory \"{Directory}\"", directory.FullName);
		}
	}
}
