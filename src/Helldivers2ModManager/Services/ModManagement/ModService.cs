using Helldivers2ModManager.Exceptions;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services.Infrastructure;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using SharpSevenZip;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
	// Guid/路径索引：与 _mods 同步维护，仅作 O(1) 查找加速；列表本身保持顺序权威（SortOrder/部署顺序）。
	private readonly Dictionary<Guid, ModData> _modsByGuid = new();
	private readonly Dictionary<string, ModData> _modsByPath = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<Guid, ModViewModel> _modViewModelCache = new();
	private readonly FileHashRepository _fileHashRepository;
	private readonly ModHashService _modHashService;
	private readonly LocalizationService _localizationService;
	private readonly VersionCheckService _versionCheckService;
	private readonly ModLinkRepository _modLinkRepository;
	private readonly GameProcessService _gameProcessService;
	private SettingsService? _settingsService;

	public ModService(ILogger<ModService> logger, FileHashRepository fileHashRepository, ModHashService modHashService, LocalizationService localizationService, VersionCheckService versionCheckService, ModLinkRepository modLinkRepository, GameProcessService gameProcessService)
	{
		_logger = logger;
		_fileHashRepository = fileHashRepository;
		_modHashService = modHashService;
		_localizationService = localizationService;
		_versionCheckService = versionCheckService;
		_modLinkRepository = modLinkRepository;
		_gameProcessService = gameProcessService;
		_mods = new();
	}

	/// <summary>
	/// 部署、清理都会写游戏目录下的文件；游戏运行时执行会与游戏读写冲突（句柄占用、补丁链读到一半）。
	/// 删除走 <see cref="RequiresGameClosedForRemoval"/> 按模组条件拦截。
	/// </summary>
	private void GuardGameNotRunning()
	{
		if (_gameProcessService.IsGameRunning())
		{
			_logger.LogWarning("Blocked a game-directory write operation because helldivers2.exe is running");
			throw new InvalidOperationException(_localizationService["ModService.GameRunningBlocked"]);
		}
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
				if (_modsByGuid.ContainsKey(mod.Manifest.Guid))
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
				_modsByGuid[mod.Manifest.Guid] = mod;
				_modsByPath[mod.Directory.FullName] = mod;
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
	
	public async Task RemoveAsync(ModData mod)
	{
		GuardInitialized();
		if (RequiresGameClosedForRemoval(mod))
			GuardGameNotRunning();

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
		_modsByGuid.Remove(removedMod.Manifest.Guid);
		_modsByPath.Remove(removedMod.Directory.FullName);

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

		// 同步清理它部署到 bin\HD2PhysBone 的托管参数目录（删除即卸载）
		try
		{
			CleanupPhysBoneParamsForMod(removedMod);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean up PhysBone parameter directories for mod \"{Name}\"", removedMod.Manifest.Name);
		}

		var recycleOption = _settingsService.DeleteToRecycleBin ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
		await Task.Run(() => FileSystem.DeleteDirectory(removedMod.Directory.FullName, UIOption.OnlyErrorDialogs, recycleOption));

		_logger.LogInformation("Mod {} removed", removedMod.Manifest.Name);
	}

	/// <summary>
	/// 删除模组是否要求游戏已关闭：只有带 HD2PhysBone 参数集（动态骨骼/自定义骨骼 rig 文件）
	/// 且参数目录已实际部署到 bin\HD2PhysBone 的模组才需要——add-on 运行中会读取这些参数文件，
	/// 删除会与之冲突。普通补丁游戏仅在启动时整段读取，运行中删除不冲突（目录名按部署时同一套
	/// 规则计算，与 <see cref="CleanupPhysBoneParamsForMod"/> 的实际写入面一致）。
	/// </summary>
	private bool RequiresGameClosedForRemoval(ModData mod)
	{
		var sets = DetectPhysBoneParamSets(mod.Directory, mod.Manifest.Name, mod.Manifest.Guid.ToString("N"));
		if (sets.Count == 0)
			return false;

		var physBoneRoot = new DirectoryInfo(Path.Combine(_settingsService!.GameDirectory, "bin", "HD2PhysBone"));
		if (!physBoneRoot.Exists)
			return false;

		return sets.Any(set =>
		{
			var dir = new DirectoryInfo(Path.Combine(physBoneRoot.FullName, set.DirName));
			return dir.Exists && IsManagedPhysBoneDir(dir);
		});
	}

	private static readonly string[] PatchFileSuffixes = ["", ".gpu_resources", ".stream"];

	/// <summary>
	/// 把某资源名在 data 目录中的补丁链左移补成 0..N-1 连续。
	/// 移动按 fromIndex 升序执行：目标位要么本来就是空洞、要么已被前一次移动腾出，因此不需要覆盖。
	/// </summary>
	internal static async Task CompactPatchChainAsync(DirectoryInfo dataDir, string baseName, ILogger logger)
	{
		var moves = PlanPatchChainCompact(EnumeratePatchChainIndexes(dataDir, baseName));
		foreach (var (fromIndex, toIndex) in moves)
		{
			foreach (var suffix in PatchFileSuffixes)
			{
				var source = Path.Combine(dataDir.FullName, $"{baseName}.patch_{fromIndex}{suffix}");
				if (!File.Exists(source))
					continue;
				var destination = Path.Combine(dataDir.FullName, $"{baseName}.patch_{toIndex}{suffix}");
				await Task.Run(() => File.Move(source, destination));
				logger.LogInformation("Compacted patch chain \"{Name}\": patch_{From} -> patch_{To}{Suffix}", baseName, fromIndex, toIndex, suffix);
			}
		}
	}

	/// <summary>枚举 data 目录中某资源名的补丁链文件（含 sidecar），返回仍存在的部署 index 集合。</summary>
	internal static HashSet<int> EnumeratePatchChainIndexes(DirectoryInfo dataDir, string baseName)
	{
		var indexes = new HashSet<int>();
		foreach (var file in dataDir.GetFiles($"{baseName}.patch_*"))
		{
			var match = GetPatchIndexRegex().Match(file.Name);
			if (!match.Success)
				continue;
			indexes.Add(int.Parse(match.Groups[1].ValueSpan));
		}
		return indexes;
	}

	/// <summary>
	/// 计算把补丁链压缩为 0..N-1 连续的左移计划（游戏按 patch_0..N 连续读取，遇第一个空洞即停止）。
	/// 输入为删除后仍存在的部署 index；返回 (fromIndex, toIndex) 列表，调用方必须按返回顺序（fromIndex 升序）执行。
	/// </summary>
	internal static List<(int FromIndex, int ToIndex)> PlanPatchChainCompact(IReadOnlyCollection<int> remainingIndexes)
	{
		var sorted = remainingIndexes.Distinct().OrderBy(static i => i).ToArray();
		var moves = new List<(int FromIndex, int ToIndex)>(sorted.Length);
		for (int target = 0; target < sorted.Length; target++)
		{
			if (sorted[target] != target)
				moves.Add((sorted[target], target));
		}
		return moves;
	}

	/// <summary>HD2PhysBone 参数集的三个必备文件名（add-on 从 bin\HD2PhysBone\&lt;目录名&gt;\ 加载）。</summary>
	internal static readonly string[] PhysBoneParamFileNames = [PhysBoneParamLocator.RigFileName, PhysBoneParamLocator.NeedleFileName, PhysBoneParamLocator.LuaUnitsFileName];

	/// <summary>add-on 额外读取的可选文件名。</summary>
	private static readonly string[] PhysBoneOptionalFileNames = [PhysBoneParamLocator.GroundFileName];

	/// <summary>管理器部署的参数目录内的标记文件；对账清理只删除带此标记的目录，保护管理器之外手动安装的参数。</summary>
	internal const string PhysBoneManagedMarkerFileName = ".hd2mm-managed";

	/// <summary>
	/// 在模组目录中查找 HD2PhysBone 参数集：同时包含三个必备参数文件的目录（递归，逐集独立）。
	/// 参数目录名优先取 mod.json 的 name 字段（从参数目录向上查到模组根目录，取最近者），否则回退清单名称。
	/// 名称非法（含路径非法字符、清空、或以 _/. 开头——add-on 会跳过这类目录）时回退模组 GUID。
	/// </summary>
	internal static List<(DirectoryInfo ParamDir, string DirName)> DetectPhysBoneParamSets(DirectoryInfo modDirectory, string fallbackName, string fallbackGuid)
	{
		var sets = new List<(DirectoryInfo, string)>();
		foreach (var paramDir in PhysBoneParamLocator.FindParamSetDirectories(modDirectory))
		{
			var requestedName = FindPhysBoneRequestedName(paramDir, modDirectory);
			var dirName = SanitizePhysBoneDirName(requestedName);
			if (string.IsNullOrEmpty(dirName))
				dirName = SanitizePhysBoneDirName(fallbackName);
			if (string.IsNullOrEmpty(dirName))
				dirName = fallbackGuid;
			sets.Add((paramDir, dirName));
		}
		return sets;
	}

	/// <summary>从参数目录向上查找到模组根目录（含），返回最近的 mod.json 中声明的 name；找不到返回 null。</summary>
	private static string? FindPhysBoneRequestedName(DirectoryInfo paramDir, DirectoryInfo modDirectory)
	{
		var rootFullName = modDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		for (var dir = paramDir; dir is not null; dir = dir.Parent)
		{
			var modJson = Path.Combine(dir.FullName, "mod.json");
			if (File.Exists(modJson))
			{
				try
				{
					using var document = JsonDocument.Parse(File.ReadAllText(modJson));
					if (document.RootElement.ValueKind == JsonValueKind.Object &&
						document.RootElement.TryGetProperty("name", out var name) &&
						name.ValueKind == JsonValueKind.String)
					{
						var value = name.GetString();
						if (!string.IsNullOrWhiteSpace(value))
							return value;
					}
				}
				catch (JsonException)
				{
					// mod.json 不是合法 JSON 时继续向上找
				}
			}

			if (dir.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(rootFullName, StringComparison.OrdinalIgnoreCase))
				break;
		}
		return null;
	}

	/// <summary>
	/// 清洗参数目录名：剔除路径非法字符与首尾空白，结尾的点和空格一并去掉（Windows 目录名限制）。
	/// 目录名为空或以 _/. 开头（add-on 跳过这类目录）时返回空串，由调用方回退。
	/// </summary>
	internal static string SanitizePhysBoneDirName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return string.Empty;

		var invalidChars = Path.GetInvalidFileNameChars();
		var cleaned = new string(name.Trim().Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
		if (cleaned.Length == 0 || cleaned.StartsWith('_') || cleaned.StartsWith('.'))
			return string.Empty;
		return cleaned;
	}

	/// <summary>判断参数目录是否由管理器部署（含托管标记文件）。</summary>
	private static bool IsManagedPhysBoneDir(DirectoryInfo dir)
	{
		return File.Exists(Path.Combine(dir.FullName, PhysBoneManagedMarkerFileName));
	}

	/// <summary>刷新模组的结果摘要：problems 为问题清单，Added/Updated/Removed 为各类变更数量。</summary>
	public sealed record RefreshModsResult(ModProblem[] Problems, int AddedCount, int UpdatedCount, int RemovedCount)
	{
		public bool HasChanges => AddedCount + UpdatedCount + RemovedCount > 0;
	}

	/// <summary>
	/// 刷新模组：与 Mods 目录做一次完整对账，而非只加载新增目录。
	/// 1) 目录已消失的模组从列表移除（保留数据库配置记录，AutoRemoveMissingMods 在下次启动清理）；
	/// 2) 目录未加载的模组按新增加载（回收站恢复/手动放入等场景）；
	/// 3) 已加载模组的 manifest.json 发生变化时重新解析并应用（名称/描述/图标/选项即时刷新，
	///    启用状态与选项选择由 Manifest setter 的选项同步逻辑保留）。
	/// </summary>
	public async Task<RefreshModsResult> RefreshModsAsync(CancellationToken cancellationToken = default)
	{
		GuardInitialized();

		var problems = new List<ModProblem>();
		var addedCount = 0;
		var updatedCount = 0;
		var removedCount = 0;

		_logger.LogInformation("Refreshing Mods directory...");

		var modsDir = new DirectoryInfo(Path.Combine(_settingsService.StorageDirectory, "Mods"));
		if (!modsDir.Exists)
		{
			_logger.LogInformation("Mods directory does not exist, nothing to refresh");
			return new RefreshModsResult([], 0, 0, 0);
		}

		var dirs = modsDir.GetDirectories();
		_logger.LogInformation("Found {} folders in Mods directory", dirs.Length);
		var loadedDirs = new HashSet<string>(dirs.Select(static d => d.FullName), StringComparer.OrdinalIgnoreCase);

		// 1) 移除目录已消失的模组。只做内存/缓存清理与事件通知：
		//    部署文件清理属于 RemoveAsync（针对"卸载"），目录都没了无需也无法执行。
		for (var index = _mods.Count - 1; index >= 0; index--)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var mod = _mods[index];
			if (loadedDirs.Contains(mod.Directory.FullName))
				continue;

			_logger.LogInformation("Removing mod \"{}\" whose directory no longer exists: {}", mod.Manifest.Name, mod.Directory.FullName);
			_mods.RemoveAt(index);
			_modsByGuid.Remove(mod.Manifest.Guid);
			_modsByPath.Remove(mod.Directory.FullName);
			ModRemoved?.Invoke(mod);
			removedCount++;

			try
			{
				await _modHashService.DeleteForModAsync(mod);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to clean up hash cache for removed mod \"{}\"", mod.Manifest.Name);
			}
		}

		foreach (var dir in dirs)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_modsByPath.TryGetValue(dir.FullName, out var existingMod))
			{
				// 2) 已加载：检测 manifest 是否变化
				switch (await ReloadManifestIfChangedAsync(existingMod, dir, problems, cancellationToken))
				{
					case ModReloadOutcome.Updated:
						updatedCount++;
						break;
					case ModReloadOutcome.GuidChanged:
						// 目录被替换成另一个模组：旧条目已移除，新清单走新增流程
						if (TryAddModDirectory(dir, problems, cancellationToken))
							addedCount++;
						break;
				}
				continue;
			}

			if (TryAddModDirectory(dir, problems, cancellationToken))
				addedCount++;
		}

		_logger.LogInformation("Refresh complete: {} added, {} updated, {} removed", addedCount, updatedCount, removedCount);
		return new RefreshModsResult(problems.ToArray(), addedCount, updatedCount, removedCount);
	}

	/// <summary>单个已加载模组清单重载的结果。</summary>
	private enum ModReloadOutcome
	{
		/// <summary>清单未变化或无法安全应用（保持现状）。</summary>
		Unchanged,
		/// <summary>清单已更新并应用到 ModData。</summary>
		Updated,
		/// <summary>清单的 Guid 发生变化（目录被替换成另一个模组），旧条目已移除，调用方应走新增流程。</summary>
		GuidChanged,
	}

	/// <summary>
	/// 重新解析模组目录的 manifest，与当前加载的清单对比，发生变化时应用新清单。
	/// 比较使用两份清单的规范化序列化输出（同一实现序列化，等价即相等），
	/// 避免直接比对文件原文受格式化/字段顺序影响。
	/// </summary>
	private async Task<ModReloadOutcome> ReloadManifestIfChangedAsync(ModData existingMod, DirectoryInfo dir, List<ModProblem> problems, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
		if (!manifestFile.Exists)
		{
			// 目录还在但清单没了：视为损坏/被破坏，报问题但不自动移除（保守处理）
			_logger.LogWarning("Manifest no longer found in \"{}\"", dir.FullName);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.NoManifestFound,
			});
			return ModReloadOutcome.Unchanged;
		}

		IModManifest newManifest;
		try
		{
			newManifest = ModManifest.DeserializeFromFile(manifestFile);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Unable to re-parse manifest \"{}\"; keeping the loaded manifest", manifestFile.FullName);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.CantParseManifest,
			});
			return ModReloadOutcome.Unchanged;
		}

		// Guid 变化：目录被替换成另一个模组。移除旧条目，由调用方按新增流程加载新清单。
		if (newManifest.Guid != existingMod.Manifest.Guid)
		{
			_logger.LogInformation("Mod at \"{}\" changed its guid from {} to {}; reloading as a new mod", dir.FullName, existingMod.Manifest.Guid, newManifest.Guid);

			var index = _mods.IndexOf(existingMod);
			if (index >= 0)
				_mods.RemoveAt(index);
			_modsByGuid.Remove(existingMod.Manifest.Guid);
			_modsByPath.Remove(existingMod.Directory.FullName);
			ModRemoved?.Invoke(existingMod);

			try
			{
				await _modHashService.DeleteForModAsync(existingMod);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to clean up hash cache for replaced mod \"{}\"", existingMod.Manifest.Name);
			}
			return ModReloadOutcome.GuidChanged;
		}

		if (SerializeCanonical(newManifest) == SerializeCanonical(existingMod.Manifest))
			return ModReloadOutcome.Unchanged;

		_logger.LogInformation("Manifest changed for mod \"{}\" ({}), applying", newManifest.Name, newManifest.Guid);
		if (!CheckPaths(newManifest, problems, dir, manifestFile))
			return ModReloadOutcome.Unchanged;

		// Manifest setter 会同步选项数组（新增选项默认启用、缺失选项截断），
		// ModViewModel 订阅了 ModData.PropertyChanged，名称/描述/选项/图标会自动刷新。
		existingMod.Manifest = newManifest;
		return ModReloadOutcome.Updated;
	}

	/// <summary>
	/// 解析并加载一个尚未加载的模组目录（刷新时新增，与启动加载共享同一套校验与去重逻辑）。
	/// 返回是否成功加载。
	/// </summary>
	private bool TryAddModDirectory(DirectoryInfo dir, List<ModProblem> problems, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		_logger.LogDebug("Processing new folder \"{}\"", dir.FullName);

		var manifestFile = new FileInfo(Path.Combine(dir.FullName, "manifest.json"));
		if (!manifestFile.Exists)
		{
			_logger.LogWarning("No manifest found in \"{}\"", dir.FullName);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.NoManifestFound,
			});
			return false;
		}

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
			return false;
		}
		catch (EndOfLifeException)
		{
			_logger.LogError("Manifest \"{}\" is unsupported version 2", manifestFile.FullName);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.OutOfSupportManifest,
			});
			return false;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unable to parse manifest \"{}\"", manifestFile.FullName);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.CantParseManifest,
			});
			return false;
		}

		if (_modsByGuid.ContainsKey(manifest.Guid))
		{
			_logger.LogError("Mod \"{}\" has a duplicate guid of \"{}\"", dir.FullName, manifest.Guid);
			problems.Add(new ModProblem
			{
				Directory = dir,
				Kind = ModProblemKind.Duplicate,
			});
			return false;
		}

		if (!CheckPaths(manifest, problems, dir, manifestFile))
			return false;

		var mod = new ModData(dir, manifest);
		_mods.Add(mod);
		_modsByGuid[mod.Manifest.Guid] = mod;
		_modsByPath[mod.Directory.FullName] = mod;
		ModAdded?.Invoke(mod);

		_logger.LogInformation("Added mod \"{}\" ({})", manifest.Name, manifest.Guid);

		_modHashService.ComputeAndStoreForModAsync(mod);
		return true;
	}

	/// <summary>把清单序列化为规范化 JSON 字符串，用于变更比较。</summary>
	private static string SerializeCanonical(IModManifest manifest)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, SkipValidation = true }))
			manifest.Serialize(writer);
		return Encoding.UTF8.GetString(stream.ToArray());
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

	public async Task PurgeAsync()
	{
		GuardInitialized();
		GuardGameNotRunning();

		await PurgeCoreAsync();
	}

	/// <summary>清理 data 目录中的全部补丁文件，不带游戏运行守卫（部署流程已在外层统一检查）。</summary>
	private async Task PurgeCoreAsync()
	{
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

		// data 已全部清空：托管参数目录必为残留（add-on 枚举 bin\HD2PhysBone 即加载，残留 rig 白占资源）。
		// 期望集合为空 = 清理全部托管目录；无托管标记的目录（管理器之外手动安装）不动。
		ReconcilePhysBoneParamDirectories(
			new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "bin", "HD2PhysBone")),
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		_logger.LogInformation("Purge complete");
	}

	public ModData? GetModByGuid(Guid guid)
	{
		return _modsByGuid.TryGetValue(guid, out var mod) ? mod : null;
	}

	public ModViewModel GetOrCreateModViewModel(ModData mod, ILogger logger, SettingsService settingsService, Services.Nexus.INexusModsService nexusModsService)
	{
		return _modViewModelCache.GetOrAdd(mod.Manifest.Guid, _ => new ModViewModel(mod, logger, settingsService, nexusModsService, _localizationService, _versionCheckService, _modLinkRepository));
	}

	/// <summary>
	/// 批量获取/创建 ModViewModel。传入预取的链接字典（ModLinkRepository.GetLinks）
	/// 时，每个 VM 不再各自开连接查询链接；已缓存的 VM 直接复用，不受字典影响。
	/// </summary>
	public List<ModViewModel> GetOrCreateModViewModels(
		IEnumerable<ModData> mods,
		ILogger logger,
		SettingsService settingsService,
		Services.Nexus.INexusModsService nexusModsService,
		IReadOnlyDictionary<Guid, string?> prefetchedLinks)
	{
		var result = new List<ModViewModel>();
		foreach (var mod in mods)
		{
			result.Add(_modViewModelCache.GetOrAdd(mod.Manifest.Guid, _ => new ModViewModel(
				mod, logger, settingsService, nexusModsService, _localizationService, _versionCheckService, _modLinkRepository, prefetchedLinks)));
		}
		return result;
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
					// 清单内容类问题只警告，不阻止导入。
					_logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptyOptions,
					});
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
					// 清单内容类问题只警告，不阻止导入。
					_logger.LogWarning("Empty Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptyOptions,
					});
				}

				if (opts.Any(static opt => opt.SubOptions is { Count: 0 }))
				{
					_logger.LogWarning("Empty Sub-Options found in manifest \"{}\"", manifestFile.FullName);
					problems.Add(new ModProblem
					{
						Directory = dir,
						Kind = ModProblemKind.EmptySubOptions,
					});
				}

				// 空 Include 列表（或空 include 路径）是模组作者故意留出的“关闭”占位
				// 选项，属于合法清单；不报告问题，也不阻止导入。

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
