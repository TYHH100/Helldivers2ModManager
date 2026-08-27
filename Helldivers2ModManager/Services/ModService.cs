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

[RegisterService(ServiceLifetime.Singleton)]
internal sealed partial class ModService
{
	internal readonly struct PatchFileTriplet
	{
		public FileInfo? Patch { get; init; }

		public FileInfo? GpuResources { get; init; }

		public FileInfo? Stream { get; init; }
	}

	[MemberNotNullWhen(true, nameof(_settingsService))]
	public bool Initialized { get; private set; }

	public IReadOnlyList<ModData> Mods => _mods;

	public event Action<ModData>? ModAdded;

	public event Action<ModData>? ModRemoved;

	private readonly ILogger<ModService> _logger;
	private readonly List<ModData> _mods;
	private readonly ConcurrentDictionary<Guid, ModViewModel> _modViewModelCache = new();
	private readonly FileHashRepository _fileHashRepository;
	private readonly ModHashService _modHashService;
	private readonly LocalizationService _localizationService;
	private readonly VersionCheckService _versionCheckService;
	private readonly ModLinkRepository _modLinkRepository;
	private SettingsService? _settingsService;

	public ModService(ILogger<ModService> logger, FileHashRepository fileHashRepository, ModHashService modHashService, LocalizationService localizationService, VersionCheckService versionCheckService, ModLinkRepository modLinkRepository)
	{
		_logger = logger;
		_fileHashRepository = fileHashRepository;
		_modHashService = modHashService;
		_localizationService = localizationService;
		_versionCheckService = versionCheckService;
		_modLinkRepository = modLinkRepository;
		_mods = new();
	}
	
	public ModProblem[] Init(SettingsService settings)
	{
		if (Initialized)
			return [];

		if (!settings.Validate())
			throw new ArgumentException("Settings are invalid!", nameof(settings));

		var problems = new List<ModProblem>();

		_settingsService = settings;
		_logger.LogInformation("Initializing mod service");

		var modsDir = new DirectoryInfo(Path.Combine(_settingsService.StorageDirectory, "Mods"));

		_logger.LogDebug("Checking \"Mods\" directroy existance");
		if (modsDir.Exists)
			_logger.LogDebug("Found \"Mods\" directory");
		else
		{
			_logger.LogDebug("Creating \"Mods\" directory");
			modsDir.Create();
		}

		var dirs = modsDir.GetDirectories();
		_logger.LogInformation("Found {} folders in \"Mods\" directory", dirs.Length);

		// 每个目录的解析（manifest 读取/反序列化 + 路径校验）互不依赖且以磁盘 IO 为主，
		// 并行执行显著缩短启动加载时间；结果按目录顺序存放，汇总阶段保持确定性顺序。
		var parsedResults = new (DirectoryInfo Dir, ModData? Mod, ModProblem[] Problems)[dirs.Length];
		Parallel.For(0, dirs.Length, index =>
		{
			parsedResults[index] = ParseModDirectory(dirs[index]);
		});

		foreach (var (dir, mod, dirProblems) in parsedResults)
		{
			problems.AddRange(dirProblems);

			// 重复 GUID 是跨目录检查，必须放在汇总（单线程）阶段进行
			if (mod is not null)
			{
				if (_mods.Any(data => data.Manifest.Guid == mod.Manifest.Guid))
				{
					_logger.LogError("Mod \"{}\" has a duplicate guid of \"{}\"", dir.FullName, mod.Manifest.Guid);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.Duplicate,
					});
					continue; // skip
				}

				_mods.Add(mod);
			}
		}

		Initialized = true;
		_logger.LogInformation("Loaded {} mods", _mods.Count);
		_logger.LogInformation("Mod service initialization complete");

		// 初始化哈希管理服务并触发版本迁移（为新版用户自动计算所有现有模组的文件哈希值）
		_modHashService.Init(_settingsService);
		// 哈希迁移是 CPU/IO 密集操作，放到后台线程执行，避免阻塞 UI 线程
		_ = Task.Run(async () => await _modHashService.MigrateExistingModsAsync(_mods));

		return problems.ToArray();
	}

	/// <summary>
	/// 解析单个模组目录的清单并校验路径（仅在后台并行加载阶段调用）。
	/// </summary>
	private (DirectoryInfo Dir, ModData? Mod, ModProblem[] Problems) ParseModDirectory(DirectoryInfo dir)
	{
		var problems = new List<ModProblem>();

		_logger.LogDebug("Processing \"{}\"", dir.FullName);

		_logger.LogDebug("Checking for \"manifest.json\"");
		var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
		if (manifestFile.Exists)
		{
			IModManifest manifest;

			try
			{
				_logger.LogDebug("Parsing manifest");
				manifest = ModManifest.DeserializeFromFile(manifestFile);
			}
			catch (UnknownManifestVersionException)
			{
				_logger.LogError("Manifest \"{}\" has unknown", manifestFile.FullName);
				problems.Add(new ModProblem
				{
					Directory = dir,
					Kind = ModProblemKind.UnknownManifestVersion,
				});
				return (dir, null, problems.ToArray());
			}
			catch (EndOfLifeException)
			{
				_logger.LogError("Manifest \"{}\" is unsupported version 2", manifestFile.FullName);
				problems.Add(new ModProblem
				{
					Directory = dir,
					Kind = ModProblemKind.OutOfSupportManifest,
				});
				return (dir, null, problems.ToArray());
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unable to parse manifest \"{}\"", manifestFile.FullName);
				problems.Add(new ModProblem
				{
					Directory = dir,
					Kind = ModProblemKind.CantParseManifest,
				});
				return (dir, null, problems.ToArray());
			}

			if (!CheckPaths(manifest, problems, dir, manifestFile))
				return (dir, null, problems.ToArray());

			return (dir, new ModData(dir, manifest), problems.ToArray());
		}

		_logger.LogWarning("No manifest found in \"{}\", deleting", dir.FullName);
		problems.Add(new ModProblem
		{
			Directory = dir,
			Kind = ModProblemKind.NoManifestFound,
		});
		dir.Delete(true);
		return (dir, null, problems.ToArray());
	}
	
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
			
			var existingMod = _mods.FirstOrDefault(m => m.Directory.FullName == modDir.FullName);
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
			}
		}
		modDir.Parent?.Create();
		await Task.Run(() => tmpDir.CopyTo(modDir.FullName));
		modDir.Refresh();

		_logger.LogInformation("Adding mod");
		var mod = new ModData(modDir, manifest);
		_mods.Add(mod);
		ModAdded?.Invoke(mod);

		// 后台异步计算并存储新模组的文件哈希值（fire-and-forget，不阻塞导入流程）
		_modHashService.ComputeAndStoreForModAsync(mod);

		tmpDir.Delete(true);
		return problems.ToArray();
	}

	public async Task RemoveAsync(ModData mod)
	{
		GuardInitialized();

		_logger.LogInformation("Attempting to remove {}", mod.Manifest.Guid);

		// 使用 GUID 查找而不是引用相等性，避免因 ModData 引用不匹配导致删除失败
		var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
		if (index < 0)
		{
			_logger.LogInformation("Removal unsuccessful");
			return;
		}
		var removedMod = _mods[index];
		_mods.RemoveAt(index);

		ModRemoved?.Invoke(removedMod);

		// 清理数据库中的文件哈希缓存
		try
		{
			await _modHashService.DeleteForModAsync(removedMod);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to delete file hash cache for mod \"{Name}\"", removedMod.Manifest.Name);
		}

		// 该模组已部署时，自动清理游戏 data 目录中由它部署的补丁文件
		await CleanupDeployedFilesForModAsync(removedMod);

		var recycleOption = _settingsService.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
		await Task.Run(() => FileSystem.DeleteDirectory(removedMod.Directory.FullName, UIOption.OnlyErrorDialogs, recycleOption));

		_logger.LogInformation("Mod {} removed", removedMod.Manifest.Name);
	}

	/// <summary>
	/// 删除模组时清理游戏 data 目录中由该模组部署的补丁文件（含 gpu_resources/stream 伴生文件）。
	/// 仅处理启用状态下实际会部署的文件；被其他仍启用模组使用的同名资源跳过。
	/// </summary>
	private async Task CleanupDeployedFilesForModAsync(ModData mod)
	{
		try
		{
			if (!mod.Enabled)
				return;

			var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
			if (!dataDir.Exists)
				return;

			var otherMods = _mods.Where(m => !ReferenceEquals(m, mod) && m.Enabled).ToArray();
			var otherFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var other in otherMods)
				foreach (var file in GetSelectedPatchFiles(other))
					otherFileNames.Add(file.Name);

			foreach (var file in GetSelectedPatchFiles(mod))
			{
				if (otherFileNames.Contains(file.Name))
					continue;

				var match = GetPatchIndexRegex().Match(file.Name);
				if (!match.Success)
					continue;
				var index = int.Parse(match.Groups[1].ValueSpan);
				var baseName = file.Name[0..16];
				// 与部署逻辑一致：SkipList 中的资源名整体后移一个槽位
				var deployedIndex = index + (_settingsService.SkipList.Contains(baseName) ? 1 : 0);
				var deployedBase = Path.Combine(dataDir.FullName, $"{baseName}.patch_{deployedIndex}");

				foreach (var path in new[] { deployedBase, deployedBase + ".gpu_resources", deployedBase + ".stream" })
				{
					if (!File.Exists(path))
						continue;
					await Task.Run(() => File.Delete(path));
					_logger.LogInformation("Cleaned up deployed file \"{Path}\" for removed mod \"{Name}\"", Path.GetFileName(path), mod.Manifest.Name);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean up deployed files for mod \"{Name}\"", mod.Manifest.Name);
		}
	}

	/// <summary>
	/// 重新扫描 Mods 目录，检测并加载新增的模组（例如从回收站恢复的文件）。
	/// 已存在的模组不会被重新加载。
	/// </summary>
	public ModProblem[] RescanMods()
	{
		GuardInitialized();

		var problems = new List<ModProblem>();
		var addedCount = 0;

		_logger.LogInformation("Rescanning Mods directory...");

		var modsDir = new DirectoryInfo(Path.Combine(_settingsService.StorageDirectory, "Mods"));
		if (!modsDir.Exists)
		{
			_logger.LogInformation("Mods directory does not exist, nothing to rescan");
			return problems.ToArray();
		}

		var dirs = modsDir.GetDirectories();
		_logger.LogInformation("Found {} folders in Mods directory", dirs.Length);

		foreach (var dir in dirs)
		{
			var existingMod = _mods.FirstOrDefault(m => m.Directory.FullName == dir.FullName);
			if (existingMod != null)
			{
				_logger.LogDebug("Mod \"{}\" already loaded, skipping", dir.FullName);
				continue;
			}

			_logger.LogDebug("Processing new folder \"{}\"", dir.FullName);

			var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
			if (manifestFile.Exists)
			{
				IModManifest manifest;

				try
				{
					manifest = ModManifest.DeserializeFromFile(manifestFile);
				}
				catch (UnknownManifestVersionException)
				{
					_logger.LogError("Manifest \"{}\" has unknown version", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.UnknownManifestVersion,
					});
					continue;
				}
				catch (EndOfLifeException)
				{
					_logger.LogError("Manifest \"{}\" is unsupported version 2", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.OutOfSupportManifest,
					});
					continue;
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Unable to parse manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.CantParseManifest,
					});
					continue;
				}

				if (_mods.Any(data => data.Manifest.Guid == manifest.Guid))
				{
					_logger.LogError("Mod \"{}\" has a duplicate guid of \"{}\"", dir.FullName, manifest.Guid);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.Duplicate,
					});
					continue;
				}

				if (!CheckPaths(manifest, problems, dir, manifestFile))
					continue;

				var mod = new ModData(dir, manifest);
				_mods.Add(mod);
				ModAdded?.Invoke(mod);
				addedCount++;

				_logger.LogInformation("Added recovered mod \"{}\" ({})", manifest.Name, manifest.Guid);

				_modHashService.ComputeAndStoreForModAsync(mod);
			}
			else
			{
				_logger.LogWarning("No manifest found in \"{}\"", dir.FullName);
				problems.Add(new ModProblem
				{
					Directory = dir,
					Kind = ModProblemKind.NoManifestFound,
				});
			}
		}

		_logger.LogInformation("Rescan complete, {} new mods added", addedCount);
		return problems.ToArray();
	}

	/// <summary>
	/// 上移模组（在列表中向上移动一位）
	/// </summary>
	public bool MoveModUp(ModData mod)
	{
		GuardInitialized();

		var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
		if (index <= 0) return false; // 已经在最上面或找不到

		_mods.RemoveAt(index);
		_mods.Insert(index - 1, mod);

		_logger.LogInformation("Mod {} moved up (index: {} -> {})", mod.Manifest.Name, index, index - 1);
		return true;
	}

	/// <summary>
	/// 下移模组（在列表中向下移动一位）
	/// </summary>
	public bool MoveModDown(ModData mod)
	{
		GuardInitialized();

		var index = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
		if (index < 0 || index >= _mods.Count - 1) return false; // 已经在最下面或找不到

		_mods.RemoveAt(index);
		_mods.Insert(index + 1, mod);

		_logger.LogInformation("Mod {} moved down (index: {} -> {})", mod.Manifest.Name, index, index + 1);
		return true;
	}

	/// <summary>
	/// 将模组移动到指定位置（用于拖拽排序）
	/// </summary>
	public bool MoveModTo(ModData mod, int newIndex)
	{
		GuardInitialized();

		var oldIndex = _mods.FindIndex(m => m.Manifest.Guid == mod.Manifest.Guid);
		if (oldIndex < 0) return false;

		// 确保新索引在有效范围内
		newIndex = Math.Max(0, Math.Min(newIndex, _mods.Count - 1));

		if (oldIndex == newIndex) return false;

		_mods.RemoveAt(oldIndex);
		_mods.Insert(newIndex, mod);

		_logger.LogInformation("Mod {} moved to index {}", mod.Manifest.Name, newIndex);
		return true;
	}

	/// <summary>
	/// 交换两个模组的位置
	/// </summary>
	public bool SwapMods(ModData mod1, ModData mod2)
	{
		GuardInitialized();

		var index1 = _mods.FindIndex(m => m.Manifest.Guid == mod1.Manifest.Guid);
		var index2 = _mods.FindIndex(m => m.Manifest.Guid == mod2.Manifest.Guid);

		if (index1 < 0 || index2 < 0) return false;

		(_mods[index1], _mods[index2]) = (_mods[index2], _mods[index1]);

		_logger.LogInformation("Swapped mods: {} <-> {}", mod1.Manifest.Name, mod2.Manifest.Name);
		return true;
	}

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

	/// <param name="reportStep">每个模组开始部署时上报模组名。</param>
	/// <param name="reportStepDetail">复制期间上报当前文件进度（副标题）。</param>
	/// <param name="reportStepCompleted">模组全部文件复制完成时上报（步骤 → ✓）。</param>
	/// <param name="reportStepFailed">复制失败时上报（当前步骤 → ✗），随后重新抛出异常。</param>
	public async Task DeployAsync(
		IReadOnlyList<ModData> requestedMods,
		Action<string>? reportStep = null,
		Action<string>? reportStepDetail = null,
		Action? reportStepCompleted = null,
		Action? reportStepFailed = null)
	{
		GuardInitialized();

		if (requestedMods.Count == 0)
		{
			_logger.LogInformation("No mods enabled, skipping deployment");
			return;
		}

		await PurgeAsync();

		_logger.LogInformation("Starting deployment of {} dashboard snapshot mods", requestedMods.Count);

		var stageDir = new DirectoryInfo(Path.Combine(_settingsService.TempDirectory, "Staging"));
		_logger.LogInformation("Creating clean staging directory \"{}\"", stageDir.FullName);
		if (stageDir.Exists)
			stageDir.Delete(true);
		stageDir.Create();

		var groups = new Dictionary<string, List<PatchFileTriplet>>();
		// name → 各模组按部署顺序贡献的 triplet 区间；复制阶段按模组展开，
		// 让"正在部署: 模组"步骤与实际复制的文件一一对应（占位文件语义不变）。
		var perNameModRanges = new Dictionary<string, List<(ModData Mod, List<PatchFileTriplet> Triplets)>>(StringComparer.OrdinalIgnoreCase);
		ModData? currentMod = null;

		void AddFilesFromDir(DirectoryInfo dir)
		{
			if (!dir.Exists)
			{
				_logger.LogWarning("Directory \"{}\" does not exist, skipping", dir.FullName);
				return;
			}
			var files = dir.GetFiles().Where(static f => GetPatchFileRegex().IsMatch(f.Name)).ToArray();

			foreach (var file in files)
				_logger.LogDebug("Adding file \"{}\"", file.FullName);

			// 单遍分组（O(N)）：旧实现对每个 name×index 做三次全列表 FirstOrDefault +
			// 动态正则编译（O(N²)），大目录部署时明显变慢；语义完全一致：
			// - names / indexes 仍按原 HashSet 构建与迭代（indexes 是所有文件的 index 并集）；
			// - 某 name 缺少某 index 的文件时仍产生空 triplet，部署时以空文件占位。
			var grouped = GroupPatchFiles(files);
			foreach (var (name, list) in grouped)
			{
				if (!groups.ContainsKey(name))
					groups.Add(name, []);
				groups[name].AddRange(list);

				if (!perNameModRanges.TryGetValue(name, out var ranges))
				{
					ranges = [];
					perNameModRanges.Add(name, ranges);
				}
				ranges.Add((currentMod!, list));
			}
		}

		_logger.LogInformation("Grouping files");
		foreach (var mod in requestedMods)
		{
			_logger.LogInformation("Working on \"{}\"", mod.Manifest.Name);
			currentMod = mod;

			switch (mod.Manifest.Version)
			{
				case ManifestVersion.Legacy:
				{
					_logger.LogInformation("Mod \"{}\" has legacy manifest", mod.Manifest.Name);

					var man = (LegacyModManifest)mod.Manifest;
					var enabled = mod.EnabledOptions;
					var selected = mod.SelectedOptions;

					if (man.Options is not null)
					{
						if (selected is not int[] { Length: 1 })
						{
							_logger.LogError("Options have the wrong count");
							continue;
						}

						var dir = new DirectoryInfo(Path.Combine(mod.Directory.FullName, man.Options[selected[0]]));
						AddFilesFromDir(dir);
					}
					else
						AddFilesFromDir(mod.Directory);
				}
				break;

				case ManifestVersion.V1:
				{
					_logger.LogInformation("Mod \"{}\" has V1 manifest", mod.Manifest.Name);

					var man = (V1ModManifest)mod.Manifest;
					var enabled = mod.EnabledOptions;
					var selected = mod.SelectedOptions;

					if (man.Options is not null)
					{
						if (enabled.Length != man.Options.Count)
						{
							_logger.LogError("Enabled option counts are not equal");
							continue;
						}

						if (selected.Length != man.Options.Count)
						{
							_logger.LogError("Selected option counts are not equal");
							continue;
						}

						_logger.LogInformation("Making include list");

						var optOrder = Enumerable.Range(0, enabled.Length).ToArray();

						for (int oi = 0; oi < optOrder.Length; oi++)
						{
							int i = optOrder[oi];

							if (!enabled[i])
								continue;

							var opt = man.Options[i];

							if (opt.Include is { } incs)
								foreach (var inc in incs)
								{
									var dir = new DirectoryInfo(Path.Combine(mod.Directory.FullName, inc));
									_logger.LogInformation("Adding \"{}\"", dir.FullName);
									AddFilesFromDir(dir);
								}

							if (opt.SubOptions is { } subs)
								{
									int selectedSubIdx = selected[i];
									for (int si = 0; si < subs.Count; si++)
									{
										if (si != selectedSubIdx)
											continue;

										var sub = subs[si];
										foreach (var inc in sub.Include)
										{
											var dir = new DirectoryInfo(Path.Combine(mod.Directory.FullName, inc));
											_logger.LogInformation("Adding \"{}\"", dir.FullName);
											AddFilesFromDir(dir);
										}
										break;
									}
								}
						}
					}
					else
						AddFilesFromDir(mod.Directory);
				}
				break;

				default:
					throw new NotSupportedException("Unknown manifest version!");
			}
		}

		_logger.LogInformation("Copying files");

		// 按模组顺序展开并复制（步骤与文件对应）：
		// - 模组步骤：开始 → 顶部插入"正在部署: 模组名"，复制完成 → ✓；
		// - 复制期间副标题实时更新：小文件并行 File.Copy 并报计数，
		//   大文件（≥ 阈值）逐个分块复制并报百分比——大文件是耗时大头，
		//   牺牲少量吞吐换来可见进度，避免步骤长时间无变化；
		// - 占位文件（空 triplet）语义与原实现一致。
		var copyParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) };
		var useSymbolicLinks = _settingsService.UseSymbolicLinks;

		try
		{
			foreach (var mod in requestedMods)
			{
			reportStep?.Invoke(mod.Manifest.Name);

			var copyItems = new List<(string SourcePath, string DestinationPath, long Size)>();
			var placeholderPaths = new List<string>();
			foreach (var (name, ranges) in perNameModRanges)
			{
				int offset = _settingsService.SkipList.Contains(name) ? 1 : 0;
				int position = 0;
				foreach (var (rangeMod, triplets) in ranges)
				{
					// 同一模组可能贡献多个区间（多个 option 含同名文件），全部展开；
					// position 随所有模组的区间累积，index 与原 groups 合并展开完全一致
					if (ReferenceEquals(rangeMod, mod))
					{
						for (int i = 0; i < triplets.Count; i++)
						{
							var triplet = triplets[i];
							var index = position + i + offset;

							var newPatchPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}");
							if (triplet.Patch is not null)
								copyItems.Add((triplet.Patch.FullName, newPatchPath, triplet.Patch.Length));
							else
								placeholderPaths.Add(newPatchPath);

							var newGpuResourcesPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}.gpu_resources");
							if (triplet.GpuResources is not null)
								copyItems.Add((triplet.GpuResources.FullName, newGpuResourcesPath, triplet.GpuResources.Length));
							else
								placeholderPaths.Add(newGpuResourcesPath);

							var newStreamPath = Path.Combine(_settingsService.GameDirectory, "data", $"{name}.patch_{index}.stream");
							if (triplet.Stream is not null)
								copyItems.Add((triplet.Stream.FullName, newStreamPath, triplet.Stream.Length));
							else
								placeholderPaths.Add(newStreamPath);
						}
					}
					position += triplets.Count;
				}
			}

			// 缺失 triplet 的文件以 0 字节占位（原语义）
			foreach (var path in placeholderPaths)
			{
				using var fs = new FileStream(path, FileMode.Create);
			}

			if (copyItems.Count == 0)
			{
				reportStepCompleted?.Invoke();
				continue;
			}

			var largeItems = copyItems
				.Where(static item => item.Size >= LargeFileCopyThreshold)
				.OrderByDescending(static item => item.Size)
				.ToArray();
			var smallItems = copyItems.Where(static item => item.Size < LargeFileCopyThreshold).ToArray();
			var done = 0;
			var total = copyItems.Count;

			// 副标题按模式区分：符号链接部署是"创建符号链接"（O(1)），不是复制文件
			var fileProgressKey = useSymbolicLinks ? "ModService.LinkingFileProgress" : "ModService.CopyingFileProgress";

			// 小文件并行复制（内核态 File.Copy / 符号链接）
			await Parallel.ForEachAsync(smallItems, copyParallelOptions, (item, _) =>
			{
				CopyFile(item.SourcePath, item.DestinationPath, useSymbolicLinks);

				var d = Interlocked.Increment(ref done);
				reportStepDetail?.Invoke(_localizationService[fileProgressKey]
					.Replace("{done}", d.ToString())
					.Replace("{total}", total.ToString())
					.Replace("{name}", Path.GetFileName(item.SourcePath)));
				return ValueTask.CompletedTask;
			});

			// 大文件逐个串行复制（避免并行争抢磁盘带宽），分块上报百分比
			foreach (var item in largeItems)
			{
				if (useSymbolicLinks)
				{
					CopyFile(item.SourcePath, item.DestinationPath, true);
					var d = Interlocked.Increment(ref done);
					reportStepDetail?.Invoke(_localizationService[fileProgressKey]
						.Replace("{done}", d.ToString())
						.Replace("{total}", total.ToString())
						.Replace("{name}", Path.GetFileName(item.SourcePath)));
				}
				else
				{
					var name = Path.GetFileName(item.SourcePath);
					await CopyLargeFileWithProgressAsync(item.SourcePath, item.DestinationPath, percent =>
						reportStepDetail?.Invoke(_localizationService["ModService.CopyingLargeFile"]
							.Replace("{name}", name)
							.Replace("{percent}", percent.ToString("F0"))));
					Interlocked.Increment(ref done);
				}
			}

			reportStepCompleted?.Invoke();
			}
		}
		catch
		{
			// 复制失败：把当前（顶部）步骤标记为失败，便于在弹窗中定位出问题的模组；随后重新抛出
			reportStepFailed?.Invoke();
			throw;
		}

		_logger.LogInformation("Deployment success");
	}

	/// <summary>部署复制时按此大小区分大文件：大文件分块复制并上报百分比，小文件内核态复制。</summary>
	private const long LargeFileCopyThreshold = 32L * 1024 * 1024;

	/// <summary>
	/// 大文件分块复制并周期性上报进度（0..1）。8MB 缓冲 + 异步 IO，每 8MB 上报一次；
	/// 只用于 ≥ 阈值的文件，小文件仍走内核态 <see cref="File.Copy"/>。
	/// </summary>
	private static async Task CopyLargeFileWithProgressAsync(string sourcePath, string destinationPath, Action<double> reportProgress)
	{
		const int bufferSize = 8 * 1024 * 1024;
		await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
		await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
		var buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
		var fileLength = source.Length;
		long totalRead = 0;
		long lastReport = 0;
		while (true)
		{
			var read = await source.ReadAsync(buffer);
			if (read == 0)
				break;
			await destination.WriteAsync(buffer.AsMemory(0, read));
			totalRead += read;
			if (totalRead - lastReport >= bufferSize)
			{
				lastReport = totalRead;
				reportProgress((double)totalRead / fileLength);
			}
		}
		reportProgress(1.0);
	}

	/// <summary>
	/// 把目录内的补丁文件按 name（16 位十六进制前缀）分组为 index → 三元组列表。
	/// 纯函数（只读文件名，不访问磁盘），部署暂存与单元测试共用同一分组语义：
	/// - names / indexes 仍按原 HashSet 构建与迭代（indexes 是所有文件的 index 并集）；
	/// - 某 name 缺少某 index 的文件时仍产生空 triplet，部署时以空文件占位。
	/// </summary>
	internal static Dictionary<string, List<PatchFileTriplet>> GroupPatchFiles(IReadOnlyList<FileInfo> files)
	{
		var byName = new Dictionary<string, Dictionary<int, PatchFileTriplet>>(StringComparer.OrdinalIgnoreCase);
		var names = new HashSet<string>();
		var indexes = new HashSet<int>();
		foreach (var file in files)
		{
			var match = GetPatchIndexRegex().Match(file.Name);
			if (!match.Success)
				continue;
			var name = file.Name[0..16];
			var index = int.Parse(match.Groups[1].ValueSpan);
			names.Add(name);
			indexes.Add(index);

			if (!byName.TryGetValue(name, out var byIndex))
			{
				byIndex = [];
				byName.Add(name, byIndex);
			}

			var triplet = byIndex.TryGetValue(index, out var existing)
				? existing
				: new PatchFileTriplet();
			if (file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
				triplet = triplet with { GpuResources = file };
			else if (file.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
				triplet = triplet with { Stream = file };
			else
				triplet = triplet with { Patch = file };
			byIndex[index] = triplet;
		}

		var result = new Dictionary<string, List<PatchFileTriplet>>(StringComparer.OrdinalIgnoreCase);
		foreach (var name in names)
		{
			var byIndex = byName[name];
			var list = new List<PatchFileTriplet>();
			foreach (var index in indexes)
			{
				list.Add(byIndex.TryGetValue(index, out var triplet)
					? triplet
					: new PatchFileTriplet());
			}
			result[name] = list;
		}
		return result;
	}

	/// <summary>
	/// 返回某个模组在当前选项状态下实际会参与部署的主补丁文件。
	/// 覆盖扫描和部署共用同一组选项展开规则，避免把未选中的护甲变体误报为覆盖。
	/// </summary>
	public IReadOnlyList<FileInfo> GetSelectedPatchFiles(ModData mod)
		=> GetSelectedPatchFiles(mod, mod.EnabledOptions, mod.SelectedOptions);

	/// <summary>
	/// Expands a caller-owned option snapshot without changing the active profile. The
	/// model preview uses this overload for temporary part and variant switches.
	/// </summary>
	internal IReadOnlyList<FileInfo> GetSelectedPatchFiles(
		ModData mod,
		IReadOnlyList<bool> enabledOptions,
		IReadOnlyList<int> selectedOptions)
	{
		GuardInitialized();
		ArgumentNullException.ThrowIfNull(mod);
		ArgumentNullException.ThrowIfNull(enabledOptions);
		ArgumentNullException.ThrowIfNull(selectedOptions);

		var directories = new List<DirectoryInfo>();
		void AddDirectory(string relativePath)
		{
			var directory = new DirectoryInfo(Path.Combine(mod.Directory.FullName, relativePath));
			if (directory.Exists)
				directories.Add(directory);
		}

		switch (mod.Manifest)
		{
			case LegacyModManifest legacy:
				if (legacy.Options is { } legacyOptions)
			{
					var selected = selectedOptions.Count > 0 ? selectedOptions[0] : 0;
					if (selected >= 0 && selected < legacyOptions.Count)
						AddDirectory(legacyOptions[selected]);
				}
				else
					directories.Add(mod.Directory);
				break;

			case V1ModManifest v1:
				if (v1.Options is not { } options)
				{
					directories.Add(mod.Directory);
					break;
				}

				for (var i = 0; i < options.Count; i++)
				{
					if (i >= enabledOptions.Count || !enabledOptions[i])
						continue;

					var option = options[i];
					if (option.Include is { } includes)
						foreach (var include in includes)
							AddDirectory(include);

					if (option.SubOptions is not { } subOptions || subOptions.Count == 0)
						continue;

					var selectedSub = i < selectedOptions.Count ? selectedOptions[i] : 0;
					if (selectedSub >= 0 && selectedSub < subOptions.Count)
						foreach (var include in subOptions[selectedSub].Include)
							AddDirectory(include);
				}
				break;

			default:
				throw new NotSupportedException("Unknown manifest version!");
		}

		return directories
			.SelectMany(static directory => directory.GetFiles().Where(file => IsMainPatchFileName(file.Name)))
			.GroupBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
			.Select(static group => group.First())
			.ToArray();
	}

	internal static bool IsMainPatchFileName(string fileName) =>
		GetMainPatchFileRegex().IsMatch(fileName);

	/// <summary>
	/// 部署用单文件复制（在 Parallel.ForEachAsync 的线程池线程上执行）。
	/// 符号链接开启时创建符号链接（O(1)），否则用内核态 File.Copy（CopyFile2）。
	/// </summary>
	private void CopyFile(string sourcePath, string destinationPath, bool useSymbolicLinks)
	{
		GuardInitialized();
		
		if (useSymbolicLinks)
		{
			if (File.Exists(destinationPath))
			{
				File.Delete(destinationPath);
			}
			File.CreateSymbolicLink(destinationPath, sourcePath);
		}
		else
		{
			File.Copy(sourcePath, destinationPath, true);
		}
	}

	public async Task PurgeAsync()
	{
		GuardInitialized();

		_logger.LogInformation("Purging mods");

		var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));

		var files = dataDir.GetFiles("*.patch_*");
		_logger.LogDebug("Found {} patch files", files.Length);

		// 有界并行删除：原实现按文件数无界 Task.Run，文件较多时线程池被占满
		await Parallel.ForEachAsync(
			files,
			new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) },
			(file, _) =>
			{
				_logger.LogTrace("Attempting to delete \"{}\"", file.Name);
				file.Delete();
				_logger.LogTrace("Deleted \"{}\"", file.Name);
				return ValueTask.CompletedTask;
			});

		_logger.LogInformation("Purge complete");
	}

	public ModData? GetModByGuid(Guid guid)
	{
		foreach (var mod in _mods)
			if (mod.Manifest.Guid == guid)
				return mod;
		return null;
	}

	public ModViewModel GetOrCreateModViewModel(ModData mod, ILogger logger, SettingsService settingsService, Services.Nexus.INexusModsService nexusModsService)
	{
		return _modViewModelCache.GetOrAdd(mod.Manifest.Guid, _ => new ModViewModel(mod, logger, settingsService, nexusModsService, _localizationService, _versionCheckService, _modLinkRepository));
	}

	public void ClearModViewModelCache()
	{
		foreach (var kvp in _modViewModelCache)
		{
			kvp.Value.Dispose();
		}
		_modViewModelCache.Clear();
	}

	[MemberNotNull(nameof(_settingsService))]
	private void GuardInitialized()
	{
		if (!Initialized)
			throw new InvalidOperationException("Object not initialized!");
	}

	private bool CheckPaths(IModManifest manifest, List<ModProblem> problems, DirectoryInfo dir, FileInfo manifestFile)
	{
		bool error = false;

		_logger.LogDebug("Checking manifest paths");
		
		switch (manifest)
		{
			case LegacyModManifest { Options: { } opts } man:
			{
				if (opts.Count == 0)
				{
					_logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptyOptions,
					});
					error = true;
				}

				if (man.IconPath is not null)
				{
					if (string.IsNullOrEmpty(man.IconPath) || string.IsNullOrWhiteSpace(man.IconPath))
					{
						_logger.LogWarning("Manifest \"{}\" contains empty icon path \"{}\"", manifestFile.FullName, man.IconPath);
						problems.Add(new ModProblem
						{
							Directory = dir,
							Kind = ModProblemKind.EmptyImagePath,
							ExtraData = man.IconPath,
						});
						// 图标路径为空不阻止导入，模组功能不受影响
					}
					else if (!TryResolveManifestRelativePath(dir, man.IconPath, out var imagePath) || !File.Exists(imagePath))
					{
						_logger.LogWarning("Manifest \"{}\" contains invalid icon path \"{}\"", manifestFile.FullName, man.IconPath);
						problems.Add(new ModProblem
						{
							Directory = dir,
							Kind = ModProblemKind.InvalidImagePath,
							ExtraData = man.IconPath,
						});
						// 图标文件缺失不阻止导入，模组功能不受影响
					}
				}

				foreach (var opt in opts)
				{
					if (string.IsNullOrWhiteSpace(opt))
					{
						// 旧格式的空选项目录视为“不包含文件”的占位开关，不报错。
						_logger.LogDebug("Manifest \"{}\" contains empty option directory; skipping", manifestFile.FullName);
						continue;
					}
					if (!TryResolveManifestRelativePath(dir, opt, out var optionPath))
					{
						_logger.LogWarning("Manifest \"{}\" contains unsafe option directory \"{}\"", manifestFile.FullName, opt);
						problems.Add(new ModProblem { Directory = dir, Kind = ModProblemKind.InvalidPath, ExtraData = opt });
						error = true;
					}
					else if (!Directory.Exists(optionPath))
					{
						// 旧格式的目录选项允许作为占位开关存在。目录缺失时部署阶段
						// 会自然跳过，不应在每次启动时向用户报告为问题。
						_logger.LogDebug("Manifest \"{}\" references optional missing option directory \"{}\"; skipping", manifestFile.FullName, opt);
					}
				}
				break;
			}

			case V1ModManifest { Options: { } opts } man:
			{
				if (opts.Count == 0)
				{
					_logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptyOptions,
					});
					error = true;
				}

				if (opts.Any(static opt => opt.SubOptions is { Count: 0 }))
				{
					_logger.LogWarning("Empty Sub-Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptySubOptions,
					});
					error = true;
				}

				if (opts.Any(static opt => opt.SubOptions?.Any(static sub => sub.Include.Count == 0) ?? false))
				{
					_logger.LogWarning("Empty includes found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptyIncludes,
					});
					error = true;
				}

				if (man.IconPath is not null)
				{
					if (string.IsNullOrEmpty(man.IconPath) || string.IsNullOrWhiteSpace(man.IconPath))
					{
						_logger.LogWarning("Manifest \"{}\" contains empty icon path", manifestFile.FullName);
						problems.Add(new ModProblem
						{
							Directory = dir,
							Kind = ModProblemKind.EmptyImagePath,
							ExtraData = man.IconPath,
						});
						// 图标路径为空不阻止导入
					}
					else if (!TryResolveManifestRelativePath(dir, man.IconPath, out var iconPath) || !File.Exists(iconPath))
					{
						_logger.LogWarning("Manifest \"{}\" contains invalid icon path \"{}\"", manifestFile.FullName, man.IconPath);
						problems.Add(new ModProblem
						{
							Directory = dir,
							Kind = ModProblemKind.InvalidImagePath,
							ExtraData = man.IconPath,
						});
						// 图标文件缺失不阻止导入
					}
				}

				foreach (var opt in opts)
				{
					if (opt.Image is not null)
					{
						if (string.IsNullOrEmpty(opt.Image) || string.IsNullOrWhiteSpace(opt.Image))
						{
							_logger.LogWarning("Manifest \"{}\" contains empty option image path", manifestFile.FullName);
							problems.Add(new ModProblem
							{
								Directory = dir,
								Kind = ModProblemKind.EmptyImagePath,
							});
							// 选项图片路径为空不阻止导入
						}
						else if (!TryResolveManifestRelativePath(dir, opt.Image, out var optionImagePath) || !File.Exists(optionImagePath))
						{
							_logger.LogWarning("Manifest \"{}\" contains invalid option image path \"{}\"", manifestFile.FullName, opt.Image);
							problems.Add(new ModProblem
							{
								Directory = dir,
								Kind = ModProblemKind.InvalidImagePath,
								ExtraData = opt.Image,
							});
							// 选项图片文件缺失不阻止导入
						}
					}

					if (opt.Include is not null)
						foreach (var inc in opt.Include)
							ValidateIncludePath(inc);

					if (opt.SubOptions is not null)
						foreach (var sub in opt.SubOptions)
						{
							if (sub.Image is not null)
							{
								if (string.IsNullOrEmpty(sub.Image) || string.IsNullOrWhiteSpace(sub.Image))
								{
									_logger.LogWarning("Manifest \"{}\" contains empty sub-option image path", manifestFile.FullName);
									problems.Add(new ModProblem
									{
										Directory = dir,
										Kind = ModProblemKind.EmptyImagePath,
									});
									// 子选项图片路径为空不阻止导入
								}
								else if (!TryResolveManifestRelativePath(dir, sub.Image, out var subOptionImagePath) || !File.Exists(subOptionImagePath))
								{
									_logger.LogWarning("Manifest \"{}\" contains invalid sub-option image path \"{}\"", manifestFile.FullName, sub.Image);
									problems.Add(new ModProblem
									{
										Directory = dir,
										Kind = ModProblemKind.InvalidImagePath,
										ExtraData = sub.Image,
									});
									// 子选项图片文件缺失不阻止导入
								}
							}

							foreach (var inc in sub.Include)
								ValidateIncludePath(inc);
						}
				}
				break;
			}
		}

		_logger.LogDebug("Path check complete");

		return !error;

		void ValidateIncludePath(string include)
		{
			if (string.IsNullOrWhiteSpace(include))
			{
				// 空 include 表示“不包含文件”的空选项，属于合法占位，不作为问题。
				_logger.LogDebug("Manifest \"{}\" contains empty include path; skipping", manifestFile.FullName);
				return;
			}
			if (!TryResolveManifestRelativePath(dir, include, out var includePath))
			{
				_logger.LogWarning("Manifest \"{}\" contains unsafe include path \"{}\"", manifestFile.FullName, include);
				problems.Add(new ModProblem { Directory = dir, Kind = ModProblemKind.InvalidPath, ExtraData = include });
				error = true;
			}
			else if (!Directory.Exists(includePath))
			{
				// Include 列表常包含作者预留的空选项目录；保持安全路径校验，
				// 但将不存在的目录视为无内容，避免启动时产生无意义警告。
				_logger.LogDebug("Manifest \"{}\" references optional missing include path \"{}\"; skipping", manifestFile.FullName, include);
			}
		}
	}

	private static bool TryResolveManifestRelativePath(DirectoryInfo root, string? relativePath, out string fullPath)
	{
		fullPath = string.Empty;
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
			return false;

		try
		{
			var rootPath = Path.GetFullPath(root.FullName);
			fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
			return fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
				|| fullPath.StartsWith(Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// 导入时自动清理清单中指向不存在文件的图片路径（图标/选项图/子选项图），
	/// 避免无效图片路径在每次启动时反复报告问题。无变化时返回原实例。
	/// </summary>
	internal static IModManifest SanitizeManifestImagePaths(IModManifest manifest, DirectoryInfo dir, ILogger? logger = null)
	{
		static bool IsInvalidImagePath(string? imagePath, DirectoryInfo root)
		{
			if (string.IsNullOrWhiteSpace(imagePath))
				return true;
			return !TryResolveManifestRelativePath(root, imagePath, out var fullPath) || !File.Exists(fullPath);
		}

		switch (manifest)
		{
			case LegacyModManifest legacy when IsInvalidImagePath(legacy.IconPath, dir):
			{
				logger?.LogInformation("Sanitizing legacy manifest icon path \"{Path}\"", legacy.IconPath);
				return new LegacyModManifest
				{
					Guid = legacy.Guid,
					Name = legacy.Name,
					Description = legacy.Description,
					IconPath = null,
					Options = legacy.Options,
				};
			}

			case V1ModManifest v1:
			{
				var iconChanged = IsInvalidImagePath(v1.IconPath, dir);
				List<ModOption>? newOptions = null;
				var optionsChanged = false;
				if (v1.Options is not null)
				{
					newOptions = new List<ModOption>(v1.Options.Count);
					foreach (var opt in v1.Options)
					{
						var optImageChanged = IsInvalidImagePath(opt.Image, dir);
						var newSubs = new List<ModSubOption>();
						var subsChanged = false;
						if (opt.SubOptions is not null)
						{
							foreach (var sub in opt.SubOptions)
							{
								if (IsInvalidImagePath(sub.Image, dir))
								{
									subsChanged = true;
									newSubs.Add(new ModSubOption
									{
										Name = sub.Name,
										Description = sub.Description,
										Include = sub.Include,
										Image = null,
									});
								}
								else
								{
									newSubs.Add(sub);
								}
							}
						}

						if (optImageChanged || subsChanged)
						{
							optionsChanged = true;
							newOptions.Add(new ModOption
							{
								Name = opt.Name,
								Description = opt.Description,
								Include = opt.Include,
								Image = optImageChanged ? null : opt.Image,
								SubOptions = opt.SubOptions is null ? null : newSubs,
							});
						}
						else
						{
							newOptions.Add(opt);
						}
					}
				}

				if (!iconChanged && !optionsChanged)
					return v1;

				logger?.LogInformation("Sanitizing v1 manifest image paths for \"{Name}\"", v1.Name);
				return new V1ModManifest
				{
					Guid = v1.Guid,
					Name = v1.Name,
					Description = v1.Description,
					IconPath = iconChanged ? null : v1.IconPath,
					Options = newOptions,
					NexusData = v1.NexusData,
				};
			}

			default:
				return manifest;
		}
	}

	[GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+(\.(stream|gpu_resources))?$")]
	private static partial Regex GetPatchFileRegex();

	[GeneratedRegex(@"^[a-z0-9]{16}\.patch_[0-9]+$")]
	private static partial Regex GetMainPatchFileRegex();

	[GeneratedRegex(@"\.patch_[0-9]+")]
	private static partial Regex GetPatchRegex();

	[GeneratedRegex(@"^(?:[a-z0-9]{16}\.patch_)([0-9]+)(?:(?:\.(?:stream|gpu_resources))?)$")]
	private static partial Regex GetPatchIndexRegex();

	private static async Task MoveDirectoryContentsAsync(DirectoryInfo source, DirectoryInfo destination)
	{
		foreach (var file in source.GetFiles())
		{
			file.MoveTo(Path.Combine(destination.FullName, file.Name), true);
		}

		foreach (var dir in source.GetDirectories())
		{
			var newDir = Directory.CreateDirectory(Path.Combine(destination.FullName, dir.Name));
			await MoveDirectoryContentsAsync(dir, newDir);
		}
	}

	private static async Task<bool> AreDirectoriesEqualAsync(DirectoryInfo dir1, DirectoryInfo dir2)
	{
		var files1 = dir1.GetFiles("*", System.IO.SearchOption.AllDirectories).OrderBy(f => f.FullName).ToList();
		var files2 = dir2.GetFiles("*", System.IO.SearchOption.AllDirectories).OrderBy(f => f.FullName).ToList();

		if (files1.Count != files2.Count)
			return false;

		for (int i = 0; i < files1.Count; i++)
		{
			var relativePath1 = files1[i].FullName.Substring(dir1.FullName.Length);
			var relativePath2 = files2[i].FullName.Substring(dir2.FullName.Length);

			if (!relativePath1.Equals(relativePath2, StringComparison.OrdinalIgnoreCase))
				return false;

			if (!await AreFilesEqualAsync(files1[i], files2[i]))
				return false;
		}

		return true;
	}

	private static async Task<bool> AreFilesEqualAsync(FileInfo file1, FileInfo file2)
	{
		if (file1.Length != file2.Length)
			return false;

		using var hashAlgorithm = SHA256.Create();

		using var stream1 = file1.OpenRead();
		using var stream2 = file2.OpenRead();

		var hash1 = await hashAlgorithm.ComputeHashAsync(stream1);
		var hash2 = await hashAlgorithm.ComputeHashAsync(stream2);

		return hash1.SequenceEqual(hash2);
	}

	/// <summary>
	/// 清理目录中所有空子目录（自底向上递归遍历，只删除不含任何文件和子目录的空目录）
	/// </summary>
	private static void CleanEmptyDirectories(DirectoryInfo directory)
	{
		foreach (var subDir in directory.GetDirectories())
		{
			// 递归处理子目录
			CleanEmptyDirectories(subDir);

			// 如果子目录处理后为空，删除它
			if (subDir.GetFileSystemInfos().Length == 0)
			{
				subDir.Delete();
			}
		}
	}
}

/// <summary>
/// 模组更新的阶段枚举，用于进度报告
/// </summary>
internal enum UpdatePhase
{
	/// <summary>正在计算当前模组文件的哈希值</summary>
	HashingCurrent,
	/// <summary>正在计算新版本文件的哈希值</summary>
	HashingNew,
	/// <summary>正在比对文件差异</summary>
	Comparing,
	/// <summary>正在执行增量文件更新</summary>
	Updating,
	/// <summary>更新已完成</summary>
	Completed,
}

/// <summary>
/// 模组更新的进度信息，通过 IProgress 回调传递给调用方
/// </summary>
internal sealed class UpdateProgressInfo
{
	/// <summary>当前更新阶段</summary>
	public UpdatePhase Phase { get; init; }

	/// <summary>当前正在处理的文件相对路径</summary>
	public string? CurrentFile { get; init; }

	/// <summary>已处理的文件数量</summary>
	public int ProcessedCount { get; init; }

	/// <summary>缓存命中的文件数量（未变化文件跳过SHA-256计算）</summary>
	public int CacheHits { get; init; }

	/// <summary>当前阶段需要处理的文件总数</summary>
	public int TotalCount { get; init; }

	/// <summary>需要更新的文件总数（仅在 Updating 和 Completed 阶段有效）</summary>
	public int NeedUpdateCount { get; init; }

	/// <summary>未变化的文件数量（仅在 Completed 阶段有效）</summary>
	public int UnchangedCount { get; init; }

	/// <summary>已删除的文件数量（仅在 Completed 阶段有效）</summary>
	public int DeletedCount { get; init; }

	/// <summary>可读的进度消息文本</summary>
	public string? Message { get; init; }

	/// <summary>是否已完成所有操作</summary>
	public bool IsCompleted { get; init; }
}
