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
	/// 删除模组时清理游戏 data 目录中由该模组部署的补丁文件（含 gpu_resources/stream 伴生文件）。
	/// 仅处理启用状态下实际会部署的文件；被其他仍启用模组使用的同名资源跳过。
	/// 删除后对受影响的资源名执行补丁链补洞：游戏按 patch_0..N 连续读取，遇到第一个空洞即停止，
	/// 不补洞会让排在被删模组之后的所有同资源名模组整段失效。
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

			var affectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var file in GetSelectedPatchFiles(mod))
			{
				affectedNames.Add(file.Name[0..16]);

				if (otherFileNames.Contains(file.Name))
					continue;

				var match = GetPatchIndexRegex().Match(file.Name);
				if (!match.Success)
					continue;
				var index = int.Parse(match.Groups[1].ValueSpan);
				var baseName = file.Name[0..16];
				var deployedBase = Path.Combine(dataDir.FullName, $"{baseName}.patch_{index}");

				foreach (var path in new[] { deployedBase, deployedBase + ".gpu_resources", deployedBase + ".stream" })
				{
					if (!File.Exists(path))
						continue;
					await Task.Run(() => File.Delete(path));
					_logger.LogInformation("Cleaned up deployed file \"{Path}\" for removed mod \"{Name}\"", Path.GetFileName(path), mod.Manifest.Name);
				}
			}

			foreach (var baseName in affectedNames)
				await CompactPatchChainAsync(dataDir, baseName, _logger);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean up deployed files for mod \"{Name}\"", mod.Manifest.Name);
		}
	}

	/// <summary>
	/// 部署收尾：把本次部署模组携带的 HD2PhysBone 参数复制到 bin\HD2PhysBone\&lt;目录名&gt;\，
	/// 并对账清理带托管标记但不在本次部署集合中的参数目录（模组被取消启用、删除或改名后的残留）——
	/// add-on 枚举该目录下的所有参数目录即加载，残留 rig 会白白占用资源。
	/// 管理器之外手动安装的参数目录（无托管标记）不受影响。
	/// </summary>
	private async Task DeployPhysBoneParamsAsync(IReadOnlyList<ModData> deployedMods, Action<string>? reportStep, Action<string>? reportStepDetail, Action? reportStepCompleted)
	{
		var physBoneRoot = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "bin", "HD2PhysBone"));

		var detected = new List<(DirectoryInfo ParamDir, string DirName)>();
		foreach (var mod in deployedMods)
			detected.AddRange(DetectPhysBoneParamSets(mod.Directory, mod.Manifest.Name, mod.Manifest.Guid.ToString("N")));

		ReconcilePhysBoneParamDirectories(physBoneRoot, detected.Select(static set => set.DirName).ToHashSet(StringComparer.OrdinalIgnoreCase));

		if (detected.Count == 0)
			return;

		reportStep?.Invoke(_localizationService["ModService.PhysBoneParamsStep"]);

		physBoneRoot.Create();
		var done = 0;
		foreach (var (paramDir, dirName) in detected)
		{
			var targetDir = Path.Combine(physBoneRoot.FullName, dirName);
			Directory.CreateDirectory(targetDir);
			foreach (var fileName in PhysBoneParamFileNames.Concat(PhysBoneOptionalFileNames))
			{
				var source = Path.Combine(paramDir.FullName, fileName);
				if (!File.Exists(source))
					continue;
				await Task.Run(() => File.Copy(source, Path.Combine(targetDir, fileName), true));
				reportStepDetail?.Invoke(_localizationService["ModService.CopyingPhysBoneParams"]
					.Replace("{name}", fileName));
			}

			// 托管标记：对账清理只删除带标记的目录
			var markerPath = Path.Combine(targetDir, PhysBoneManagedMarkerFileName);
			if (!File.Exists(markerPath))
				await Task.Run(() => File.WriteAllText(markerPath, string.Empty));

			done++;
			_logger.LogInformation("Deployed PhysBone parameters \"{Dir}\" from \"{Source}\"", dirName, paramDir.FullName);
		}

		reportStepDetail?.Invoke(_localizationService["ModService.PhysBoneParamsDone"].Replace("{count}", done.ToString()));
		reportStepCompleted?.Invoke();
	}

	/// <summary>
	/// 删除 bin\HD2PhysBone 下带托管标记、且目录名不在期望集合中的参数目录；期望集合为空表示全部托管目录都要清理。
	/// </summary>
	private void ReconcilePhysBoneParamDirectories(DirectoryInfo physBoneRoot, HashSet<string> expectedNames)
	{
		if (!physBoneRoot.Exists)
			return;

		foreach (var dir in physBoneRoot.EnumerateDirectories())
		{
			if (expectedNames.Contains(dir.Name) || !IsManagedPhysBoneDir(dir))
				continue;
			try
			{
				dir.Delete(true);
				_logger.LogInformation("Removed stale managed PhysBone parameter directory \"{Name}\"", dir.Name);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to remove stale managed PhysBone parameter directory \"{Name}\"", dir.Name);
			}
		}
	}

	/// <summary>删除模组时同步清理它部署到 bin\HD2PhysBone 下的参数目录（仅托管目录）。</summary>
	private void CleanupPhysBoneParamsForMod(ModData mod)
	{
		var physBoneRoot = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "bin", "HD2PhysBone"));
		if (!physBoneRoot.Exists)
			return;

		foreach (var (_, dirName) in DetectPhysBoneParamSets(mod.Directory, mod.Manifest.Name, mod.Manifest.Guid.ToString("N")))
		{
			var dir = new DirectoryInfo(Path.Combine(physBoneRoot.FullName, dirName));
			if (!dir.Exists || !IsManagedPhysBoneDir(dir))
				continue;
			try
			{
				dir.Delete(true);
				_logger.LogInformation("Removed managed PhysBone parameter directory \"{Name}\" for removed mod \"{Mod}\"", dirName, mod.Manifest.Name);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to remove managed PhysBone parameter directory \"{Name}\"", dirName);
			}
		}
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

		GuardGameNotRunning();

		await PurgeCoreAsync();

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

					// 空选项列表与无选项等同（根目录补丁），与 GetSelectedPatchFiles 保持一致。
					if (man.Options is { Count: > 0 })
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

					// 空选项列表与无选项等同（根目录补丁），与 GetSelectedPatchFiles 保持一致。
					if (man.Options is { Count: > 0 })
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
							var index = position + i;

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

			// HD2PhysBone 参数目录生命周期：复制本次部署模组的参数到 bin\HD2PhysBone，
			// 并对账清理带托管标记的残留目录（模组被取消启用、删除或改名后）
			await DeployPhysBoneParamsAsync(requestedMods, reportStep, reportStepDetail, reportStepCompleted);
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
					// 空选项列表与无选项等同：模组根目录的补丁就是全部内容
					//（与预览页"此模组没有提供可切换的清单选项，将按当前配置预览"同语义）。
					if (legacy.Options is { Count: > 0 } legacyOptions)
				{
						var selected = selectedOptions.Count > 0 ? selectedOptions[0] : 0;
						if (selected >= 0 && selected < legacyOptions.Count)
							AddDirectory(legacyOptions[selected]);
					}
					else
						directories.Add(mod.Directory);
					break;

				case V1ModManifest v1:
					if (v1.Options is not { Count: > 0 } options)
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
}
