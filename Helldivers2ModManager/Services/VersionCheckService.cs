using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 版本检测服务
/// 参考 hd2-repatcher 的实现方式：扫描所有模组的补丁文件提取 Unit 版本号，
/// https://github.com/RaidingForPants/hd2-repatcher/
/// 以多数版本作为参考基准，标记偏离的模组。
/// v1.5.0 新增深度分析：文件结构完整性校验、Unit 内部结构分析、伴生文件检查。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed partial class VersionCheckService
{
    /// <summary>
    /// Unit 资源类型 ID（来自 hd2-repatcher 魔数）
    /// </summary>
    private const long UnitTypeId = unchecked((long)16187218042980615487UL);

    /// <summary>
    /// 补丁文件头魔数（0xF0000011），来自 HD2SDK-CommunityEdition
    /// </summary>
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);

    /// <summary>
    /// Unit 版本阈值：当版本低于此值时，需要检查 Layout Format 格式
    /// 来自 hd2-repatcher update_patch_file() 的 (v &lt; 0xA4CD36) 判断
    /// </summary>
    private const uint VersionThresholdForLayoutCheck = 0xA4CD36u;
    private const long MaxMemoryReadBytes = 512L * 1024 * 1024;
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;

    /// <summary>
    /// 缓存的参考版本号（static 跨实例），用于单模组快速检测
    /// </summary>
    private static uint? s_cachedReferenceVersion;
    private static int s_cachedModCount;
    private static int s_cachedUnitCount;

    /// <summary>
    /// 是否有缓存的参考版本号（增量检查时需要以此判断是否需要全量扫描）
    /// </summary>
    public static bool HasCachedReference => s_cachedReferenceVersion.HasValue;

    private readonly ILogger<VersionCheckService> _logger;
    private readonly LocalizationService _localizationService;

    public VersionCheckService(ILogger<VersionCheckService> logger, SettingsService settingsService, LocalizationService localizationService)
    {
        _logger = logger;
        _localizationService = localizationService;
    }

    /// <summary>
    /// 批量检查所有模组的版本兼容性。
    /// 采用"模组间横向对比"策略：
    /// 1. 扫描所有模组的补丁文件，收集所有 Unit 版本号
    /// 2. 以出现频率最高的版本作为参考版本
    /// 3. 与参考版本不一致的模组标记为不兼容
    /// </summary>
    /// <param name="mods">模组数据列表</param>
    /// <returns>模组 GUID 到检测结果的映射字典</returns>
    public async Task<Dictionary<Guid, ModVersionCheckResult>> CheckAllModsAsync(IEnumerable<ModData> mods)
    {
        var results = new ConcurrentDictionary<Guid, ModVersionCheckResult>();
        var modList = mods.ToList();

        if (modList.Count == 0)
            return [];

        _logger.LogInformation("开始扫描 {Count} 个模组的补丁文件...", modList.Count);

        // 第一步：并行提取所有模组的 Unit 版本信息
        var allModVersions = new ConcurrentDictionary<Guid, (List<uint> Versions, List<PatchUnitInfo> Infos)>();

        // 用 SemaphoreSlim 限制并发数，避免所有模组的补丁文件同时读到内存中
        // 补丁文件可能每个几十 MB，全部模组并行读取会导致内存飙升至 1GB+
        using var semaphore = new SemaphoreSlim(2, 2);

        var scanTasks = modList.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                // 只扫描主补丁文件，排除 .gpu_resources 和 .stream 等附属文件（格式不同）
                var patchFiles = mod.Directory.GetFiles("*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = f.Name;
                        return name.Contains(".patch_") &&
                               !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                               !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToArray();

                if (patchFiles.Length == 0)
                    return; // 无补丁文件，跳过

                var versions = new List<uint>();
                var infos = new List<PatchUnitInfo>();

                foreach (var pf in patchFiles)
                {
                    var unitInfos = await ExtractUnitVersionsFromPatchFileAsync(pf);
                    foreach (var info in unitInfos)
                    {
                        infos.Add(info);
                        versions.Add(info.Version);
                    }
                }

                if (versions.Count > 0)
                    allModVersions[mod.Manifest.Guid] = (versions, infos);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(scanTasks);

        // 第二步：统计所有模组的版本分布，取多数版本作为参考
        var allVersions = allModVersions.Values
            .SelectMany(v => v.Versions)
            .ToList();

        if (allVersions.Count == 0)
        {
            _logger.LogInformation("所有模组均不含 Unit 资源，全部标记为无法确认");
            s_cachedReferenceVersion = null;
            foreach (var mod in modList)
            {
                results[mod.Manifest.Guid] = new ModVersionCheckResult
                {
                    Status = ModVersionStatus.Unknown,
                    LastChecked = DateTime.Now
                };
            }
            return results.ToDictionary(k => k.Key, v => v.Value);
        }

        var referenceVersion = GetMostCommonVersion(allVersions);
        s_cachedReferenceVersion = referenceVersion;
        s_cachedModCount = allModVersions.Count;
        s_cachedUnitCount = allVersions.Count;
        _logger.LogInformation("Reference Unit version: 0x{Version:X8} (from {UnitCount} Unit entries across {ModCount} mods)",
            referenceVersion, s_cachedModCount, s_cachedUnitCount);

        // 第三步：以参考版本为基准，判定每个模组的兼容性
        foreach (var mod in modList)
        {
            if (allModVersions.TryGetValue(mod.Manifest.Guid, out var modInfo))
            {
                var allMatch = modInfo.Versions.All(v => v == referenceVersion);
                var result = new ModVersionCheckResult
                {
                    Status = allMatch ? ModVersionStatus.Compatible : ModVersionStatus.Incompatible,
                    GameVersion = referenceVersion,
                    LastChecked = DateTime.Now,
                    PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(modInfo.Infos)
                };

                results[mod.Manifest.Guid] = result;
            }
            else
            {
                // 没有补丁文件或没有 Unit 资源的模组，无法确认兼容性（音频/UI 等非模型模组）
                results[mod.Manifest.Guid] = new ModVersionCheckResult
                {
                    Status = ModVersionStatus.Unknown,
                    GameVersion = referenceVersion,
                    LastChecked = DateTime.Now
                };
            }
        }

        _logger.LogInformation("版本检查完成: {Total} 个模组", results.Count);
        return results.ToDictionary(k => k.Key, v => v.Value);
    }

    /// <summary>
    /// 对单个新增模组进行快速版本检测（使用缓存的参考版本），避免全量扫描
    /// </summary>
    public async Task<ModVersionCheckResult?> CheckSingleModAsync(ModData mod, uint? fallbackVersion = null, bool includeDetailedAnalysis = false)
    {
        var referenceVersion = s_cachedReferenceVersion ?? fallbackVersion;

        var patchFiles = mod.Directory.GetFiles("*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = f.Name;
                return name.Contains(".patch_") &&
                       !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var deepAnalysis = includeDetailedAnalysis ? await AnalyzeModPatchFilesAsync(mod.Directory) : null;

        if (patchFiles.Length == 0)
        {
            _logger.LogInformation("新增模组 {Name} 无补丁文件（可能为音频/UI 模组），标记为无法确认", mod.Manifest.Name);
            return new ModVersionCheckResult
            {
                Status = ModVersionStatus.Unknown,
                GameVersion = referenceVersion ?? 0,
                LastChecked = DateTime.Now,
                DetailedAnalysis = deepAnalysis
            };
        }

        var versions = new List<uint>();
        var infos = new List<PatchUnitInfo>();

        foreach (var pf in patchFiles)
        {
            var unitInfos = await ExtractUnitVersionsFromPatchFileAsync(pf);
            foreach (var info in unitInfos)
            {
                infos.Add(info);
                versions.Add(info.Version);
            }
        }

        if (versions.Count == 0)
        {
            _logger.LogInformation("New mod {Name} has no Unit resources, marked as indeterminate", mod.Manifest.Name);
            return new ModVersionCheckResult
            {
                Status = ModVersionStatus.Unknown,
                GameVersion = referenceVersion ?? 0,
                LastChecked = DateTime.Now,
                DetailedAnalysis = deepAnalysis
            };
        }

        if (referenceVersion.HasValue)
        {
            var allMatch = versions.All(v => v == referenceVersion.Value);
            var status = allMatch ? ModVersionStatus.Compatible : ModVersionStatus.Incompatible;
            _logger.LogInformation("New mod {Name} version check complete: {Status} (reference 0x{Ref:X8})",
                mod.Manifest.Name,
                status == ModVersionStatus.Compatible ? "Compatible" : "Incompatible",
                referenceVersion.Value);
            return new ModVersionCheckResult
            {
                Status = status,
                GameVersion = referenceVersion.Value,
                LastChecked = DateTime.Now,
                PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(infos),
                DetailedAnalysis = deepAnalysis
            };
        }
        else
        {
            // 无缓存参考版本，以自身版本作为参考
            var selfVersion = GetMostCommonVersion(versions);
            _logger.LogInformation("新增模组 {Name} 无参考版本可对比，以其自身版本 0x{Ver:X8} 作为基准",
                mod.Manifest.Name, selfVersion);
            return new ModVersionCheckResult
            {
                Status = ModVersionStatus.Compatible,
                GameVersion = selfVersion,
                LastChecked = DateTime.Now,
                PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(infos),
                DetailedAnalysis = deepAnalysis
            };
        }
    }

    /// <summary>
    /// 从单个补丁文件中提取所有 Unit 版本信息
    /// 参考 hd2-repatcher update_patch_file() 实现
    /// </summary>
    public async Task<List<PatchUnitInfo>> ExtractUnitVersionsFromPatchFileAsync(FileInfo patchFile)
    {
        var result = new List<PatchUnitInfo>();

        try
        {
            if (!patchFile.Exists || patchFile.Length < 72)
                return result;

            if (!ShouldUseMemoryRead(patchFile))
                return await ExtractUnitVersionsFromPatchFileStreamAsync(patchFile);

            var data = await File.ReadAllBytesAsync(patchFile.FullName);

            // 解析补丁文件头: magic(4) + numTypes(4) + numFiles(4) + unknown(4) + unknownData(56) = 72 bytes
            if (data.Length < 72)
                return result;

            var numTypes = MemoryMarshal.Read<int>(data.AsSpan(4, 4));
            var numFiles = MemoryMarshal.Read<int>(data.AsSpan(8, 4));

            // 类型条目偏移: 72
            var typeEntriesOffset = 72;
            // 文件条目偏移: 72 + numTypes * 32
            var fileEntriesOffset = typeEntriesOffset + numTypes * 32;

            if (fileEntriesOffset + numFiles * 80 > data.Length)
            {
                _logger.LogTrace("Patch file {File} format mismatch, skipping", patchFile.Name);
                return result;
            }

            for (int i = 0; i < numFiles; i++)
            {
                var entryOffset = fileEntriesOffset + i * 80;
                var typeId = MemoryMarshal.Read<long>(data.AsSpan(entryOffset + 8, 8));

                if (typeId == UnitTypeId)
                {
                    var fileId = MemoryMarshal.Read<long>(data.AsSpan(entryOffset, 8));
                    var dataOffset = MemoryMarshal.Read<long>(data.AsSpan(entryOffset + 16, 8));
                    var dataSize = MemoryMarshal.Read<int>(data.AsSpan(entryOffset + 56, 4));

                    if (dataOffset >= 0 && dataSize >= 0x30 && dataOffset + 0x30 <= data.Length)
                    {
                        var version = MemoryMarshal.Read<uint>(data.AsSpan((int)dataOffset + 0x2C, 4));

                        result.Add(new PatchUnitInfo
                        {
                            FileName = patchFile.Name,
                            FileId = fileId,
                            Version = version,
                            DataSize = dataSize
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析补丁文件 {File} 时出错", patchFile.Name);
        }

        return result;
    }

    private async Task<List<PatchUnitInfo>> ExtractUnitVersionsFromPatchFileStreamAsync(FileInfo patchFile)
    {
        var result = new List<PatchUnitInfo>();

        try
        {
            await using var stream = OpenPatchReadStream(patchFile);
            var header = new byte[HeaderSize];
            if (!await ReadAtAsync(stream, 0, header))
                return result;

            var numTypes = MemoryMarshal.Read<int>(header.AsSpan(4, 4));
            var numFiles = MemoryMarshal.Read<int>(header.AsSpan(8, 4));
            if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
                return result;

            var typeEntriesOffset = HeaderSize;
            var fileEntriesOffset = typeEntriesOffset + (long)numTypes * TypeEntrySize;
            var fileEntriesLength = (long)numFiles * FileEntrySize;
            if (fileEntriesOffset + fileEntriesLength > stream.Length)
            {
                _logger.LogTrace("Patch file {File} format mismatch, skipping", patchFile.Name);
                return result;
            }

            var entry = new byte[FileEntrySize];
            var versionBuffer = new byte[4];
            for (var i = 0; i < numFiles; i++)
            {
                var entryOffset = fileEntriesOffset + (long)i * FileEntrySize;
                if (!await ReadAtAsync(stream, entryOffset, entry))
                    break;

                var typeId = MemoryMarshal.Read<long>(entry.AsSpan(8, 8));
                if (typeId != UnitTypeId)
                    continue;

                var fileId = MemoryMarshal.Read<long>(entry.AsSpan(0, 8));
                var dataOffset = MemoryMarshal.Read<long>(entry.AsSpan(16, 8));
                var dataSize = MemoryMarshal.Read<int>(entry.AsSpan(56, 4));
                if (dataOffset < 0 || dataSize < 0x30 || dataOffset + 0x30 > stream.Length)
                    continue;

                if (!await ReadAtAsync(stream, dataOffset + 0x2C, versionBuffer))
                    continue;

                result.Add(new PatchUnitInfo
                {
                    FileName = patchFile.Name,
                    FileId = fileId,
                    Version = MemoryMarshal.Read<uint>(versionBuffer),
                    DataSize = dataSize
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析补丁文件 {File} 时出错", patchFile.Name);
        }

        return result;
    }

    /// <summary>
    /// 获取列表中出现频率最高的版本号
    /// </summary>
    private static uint GetMostCommonVersion(List<uint> versions)
    {
        return versions
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First()
            .Key;
    }

    // ===================================================================
    // 深度分析（v1.5.0+）
    // 参考 hd2-repatcher update_patch_file() 实现以下检查：
    // - 文件结构完整性校验（魔数、类型条目、文件条目边界）
    // - Unit 内部结构分析（LOD Group、Layout Format）
    // - 伴生文件检查（.gpu_resources / .stream）
    // ===================================================================

    /// <summary>
    /// 对单个模组的所有补丁文件执行深度分析。
    /// </summary>
    private async Task<ModDetailedAnalysis> AnalyzeModPatchFilesAsync(DirectoryInfo modDir)
    {
        var patchFiles = modDir.GetFiles("*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = f.Name;
                return name.Contains(".patch_") &&
                       !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        var analysis = new ModDetailedAnalysis
        {
            TotalPatchFiles = patchFiles.Length
        };

        if (patchFiles.Length == 0)
            return analysis;

        var patchAnalyses = new List<PatchFileAnalysis>();
        var typeDistributions = new ConcurrentDictionary<long, int>();

        foreach (var pf in patchFiles)
        {
            var pa = await AnalyzeSinglePatchFileStructureAsync(pf);
            patchAnalyses.Add(pa);

            // 统计资源类型分布
            if (pa.TotalResources > 0)
            {
                typeDistributions.AddOrUpdate(UnitTypeId, pa.UnitDetails.Count, (_, existing) => existing + pa.UnitDetails.Count);
            }
        }

        analysis.PatchFiles = patchAnalyses;
        analysis.FilesWithUnits = patchAnalyses.Count(p => p.UnitDetails.Count > 0);
        analysis.HealthyFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Healthy);
        analysis.WarningFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Warning);
        analysis.CorruptedFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Corrupted);

        // 仅对包含 Unit 资源的文件做结构性和伴生文件判定
        // 非 Unit 文件（如音频、UI 资源）可能有不同的文件格式，不适用魔数等校验
        var unitPatchFiles = patchAnalyses.Where(p => p.UnitDetails.Count > 0).ToList();
        analysis.HasStructuralIssues = unitPatchFiles.Any(p =>
            !p.HeaderValid || !p.FileEntriesInBounds || p.HealthStatus == PatchHealthStatus.Corrupted);
        analysis.HasCompanionFileIssues = unitPatchFiles.Any(p => !p.HasGpuResources || !p.HasStream);
        analysis.HasUnitStructuralIssues = unitPatchFiles.Any(p =>
            p.UnitDetails.Any(u => !u.LODGroupInBounds || !u.UnitDataInBounds ||
                                   (u.LayoutFormatChecked && !u.LayoutFormatValid)));
        analysis.HasGpuResourceIssues = unitPatchFiles.Any(p => !p.GpuResourceBoundsValid);

        analysis.ResourceTypes = typeDistributions
            .Select(kv => new ResourceTypeDistribution { TypeId = kv.Key, ResourceCount = kv.Value })
            .ToList();

        return analysis;
    }

    /// <summary>
    /// 对单个补丁文件进行结构分析。
    /// 参考 hd2-repatcher update_patch_file()：
    /// 1. 验证魔数
    /// 2. 解析类型条目统计总资源数，检测 total_resources &lt; numFiles
    /// 3. 验证每个文件条目的偏移边界
    /// 4. 检查伴生文件
    /// 5. 对 Unit 资源执行内部结构分析
    /// </summary>
    private async Task<PatchFileAnalysis> AnalyzeSinglePatchFileStructureAsync(FileInfo patchFile)
    {
        var analysis = new PatchFileAnalysis
        {
            FileName = patchFile.Name,
            FileSize = patchFile.Length,
            HealthStatus = PatchHealthStatus.Healthy
        };

        try
        {
            if (!patchFile.Exists || patchFile.Length < 72)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.FileTooSmallOrMissing"];
                return analysis;
            }

            if (!ShouldUseMemoryRead(patchFile))
                return await AnalyzeSinglePatchFileStructureStreamAsync(patchFile, analysis);

            var data = await File.ReadAllBytesAsync(patchFile.FullName);
            if (data.Length < 72)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.FileTooSmallToContainHeader"];
                return analysis;
            }

            // 1. 验证魔数
            var magic = MemoryMarshal.Read<int>(data.AsSpan(0, 4));
            if (magic != PatchHeaderMagic)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.HeaderValid = false;
                analysis.Message = _localizationService["VersionCheck.InvalidMagicNumber"].Replace("{actual}", $"0x{magic:X8}").Replace("{expected}", $"0x{PatchHeaderMagic:X8}");
                return analysis;
            }
            analysis.HeaderValid = true;

            var numTypes = MemoryMarshal.Read<int>(data.AsSpan(4, 4));
            var numFiles = MemoryMarshal.Read<int>(data.AsSpan(8, 4));
            analysis.NumTypes = numTypes;
            analysis.NumFiles = numFiles;

            // 安全检查：防止无效的 numTypes/numFiles 导致巨大的偏移计算
            if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.SuspiciousHeaderValues"].Replace("{numTypes}", numTypes.ToString()).Replace("{numFiles}", numFiles.ToString());
                return analysis;
            }

            // 2. 解析类型条目，统计总资源数
            var typeEntriesOffset = 72;
            var fileEntriesOffset = typeEntriesOffset + numTypes * 32;
            int totalResources = 0;
            bool foundUnitType = false;

            for (int i = 0; i < numTypes; i++)
            {
                var teOffset = typeEntriesOffset + i * 32;
                if (teOffset + 32 > data.Length)
                    break;

                // 类型条目结构: unknown1(8) + type_id(8) + unknown2(8) + num_resources(8)
                var typeId = MemoryMarshal.Read<long>(data.AsSpan(teOffset + 8, 8));
                var numResources = MemoryMarshal.Read<long>(data.AsSpan(teOffset + 24, 8));
                totalResources += (int)numResources;

                if (typeId == UnitTypeId)
                    foundUnitType = true;
            }
            analysis.TotalResources = totalResources;

            // 2a. 检测 hd2-repatcher 中的 corrupted 条件：total_resources < numFiles
            if (totalResources < numFiles)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.ResourceCountMismatch"].Replace("{totalResources}", totalResources.ToString()).Replace("{numFiles}", numFiles.ToString());
                return analysis;
            }

            // 如果没有 Unit 类型，标记为 NoUnitResources
            if (!foundUnitType)
            {
                analysis.HealthStatus = PatchHealthStatus.NoUnitResources;
                analysis.Message = _localizationService["VersionCheck.NoUnitResourcesInFile"];
                // 仍然检查伴生文件
                CheckCompanionFiles(patchFile, analysis);
                return analysis;
            }

            // 3. 验证文件条目偏移边界
            if (fileEntriesOffset + numFiles * 80 > data.Length)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.FileEntriesInBounds = false;
                analysis.Message = _localizationService["VersionCheck.FileEntriesExceedBounds"];
                return analysis;
            }
            analysis.FileEntriesInBounds = true;

            // 4. 检查伴生文件与 GPU 数据边界
            CheckCompanionFiles(patchFile, analysis);
            CheckGpuResourceBounds(patchFile, analysis, data, fileEntriesOffset, numFiles);

            // 5. 遍历文件条目，对每个 Unit 资源执行内部结构分析
            var unitDetails = new List<UnitResourceDetail>();
            for (int i = 0; i < numFiles; i++)
            {
                var entryOffset = fileEntriesOffset + i * 80;
                var typeId = MemoryMarshal.Read<long>(data.AsSpan(entryOffset + 8, 8));

                if (typeId != UnitTypeId)
                    continue;

                var fileId = MemoryMarshal.Read<long>(data.AsSpan(entryOffset, 8));
                var dataOffset = MemoryMarshal.Read<long>(data.AsSpan(entryOffset + 16, 8));
                var dataSize = MemoryMarshal.Read<int>(data.AsSpan(entryOffset + 56, 4));

                // 检查 dataOffset > fileSize (hd2-repatcher corrupted check)
                if (dataOffset > data.Length)
                {
                    analysis.HealthStatus = PatchHealthStatus.Corrupted;
                    analysis.Message = _localizationService["VersionCheck.UnitDataOffsetExceedsFileSize"].Replace("{fileId}", $"0x{fileId:X16}").Replace("{dataOffset}", $"0x{dataOffset:X}");
                    break;
                }

                // 执行 Unit 内部结构分析
                var unitDetail = AnalyzeUnitResourceDeep(patchFile.Name, fileId, data, (int)dataOffset, dataSize, _localizationService);
                unitDetails.Add(unitDetail);
            }

            analysis.UnitDetails = unitDetails;

            // 如果有 Unit 详情且有结构性问题，升级健康状态
            if (unitDetails.Any(u => !u.LODGroupInBounds || !u.UnitDataInBounds ||
                                     (u.LayoutFormatChecked && !u.LayoutFormatValid)))
            {
                analysis.HealthStatus = PatchHealthStatus.Warning;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分析补丁文件 {File} 结构时出错", patchFile.Name);
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.Message = _localizationService["VersionCheck.AnalysisException"].Replace("{message}", ex.Message);
        }

        return analysis;
    }

    /// <summary>
    /// 对 Unit 资源进行内部结构分析。
    /// 参考 hd2-repatcher update_patch_file()：
    /// - version &lt; 0xA4CD36 时检查 Layout Format 格式
    /// - 检查 LOD Group 偏移和大小
    /// </summary>
    private static UnitResourceDetail AnalyzeUnitResourceDeep(string fileName, long fileId, byte[] fileData, int dataOffset, int dataSize, LocalizationService loc)
    {
        var detail = new UnitResourceDetail
        {
            FileName = fileName,
            FileId = fileId,
            Version = 0,
            DataSize = dataSize,
            UnitDataInBounds = (dataOffset >= 0 && dataSize >= 0x30 && dataOffset + 0x30 <= fileData.Length)
        };

        if (!detail.UnitDataInBounds)
            return detail;

        // 读取版本号（偏移 0x2C）
        var version = MemoryMarshal.Read<uint>(fileData.AsSpan(dataOffset + 0x2C, 4));
        detail.Version = version;

        // 读取 LOD Group 偏移（0x30）和 Joint List 偏移（0x34）
        var lodGroupOffset = MemoryMarshal.Read<int>(fileData.AsSpan(dataOffset + 0x30, 4));
        var jointListOffset = MemoryMarshal.Read<int>(fileData.AsSpan(dataOffset + 0x34, 4));
        detail.LODGroupOffset = lodGroupOffset;
        detail.JointListOffset = jointListOffset;

        // 计算 LOD Group 大小并验证边界
        var lodGroupSize = jointListOffset - lodGroupOffset;
        detail.LODGroupSize = lodGroupSize;

        if (lodGroupOffset >= 0 && lodGroupSize > 0 &&
            dataOffset + lodGroupOffset + lodGroupSize <= fileData.Length)
        {
            detail.LODGroupInBounds = true;
        }

        // 如果版本低于阈值，检查 Layout Format 格式
        // 来自 hd2-repatcher: if (v < 0xA4CD36) 检查 layout 数据
        if (version < VersionThresholdForLayoutCheck)
        {
            detail.LayoutFormatChecked = true;
            int layoutFormatIssues = 0;

            // Layout 列表偏移在 0x5C 处
            if (dataSize >= 0x60)
            {
                var layoutListOffset = MemoryMarshal.Read<int>(fileData.AsSpan(dataOffset + 0x5C, 4));

                if (dataOffset + layoutListOffset + 4 <= fileData.Length)
                {
                    var numLayouts = MemoryMarshal.Read<int>(fileData.AsSpan(dataOffset + layoutListOffset, 4));
                    var layoutArrayOffset = dataOffset + layoutListOffset + 4;

                    // 安全检查：防止过多的 layout 数量导致读取越界
                    if (numLayouts > 0 && numLayouts <= 100 &&
                        layoutArrayOffset + numLayouts * 4 <= fileData.Length)
                    {
                        // 读取每个 layout 的偏移
                        var layoutOffsets = new int[numLayouts];
                        for (int li = 0; li < numLayouts; li++)
                        {
                            layoutOffsets[li] = MemoryMarshal.Read<int>(fileData.AsSpan(layoutArrayOffset + li * 4, 4));
                        }

                        // 对每个 layout，检查 16 个 item 的 format 字段
                        // 参考 hd2-repatcher: 每个 layout item 的格式: type(4) + format(4) + unknown(12) = 20 bytes
                        for (int li = 0; li < numLayouts; li++)
                        {
                            var layoutStart = dataOffset + layoutListOffset + layoutOffsets[li];
                            if (layoutStart + 8 + 16 * 20 > fileData.Length)
                                continue;

                            for (int item = 0; item < 16; item++)
                            {
                                var itemOffset = layoutStart + 8 + item * 20;
                                var itemFormat = MemoryMarshal.Read<int>(fileData.AsSpan(itemOffset + 4, 4));

                                // hd2-repatcher: if item_format > 16, it needs repair
                                if (itemFormat > 16)
                                {
                                    layoutFormatIssues++;
                                }
                            }
                        }
                    }
                }
            }

            detail.LayoutFormatValid = layoutFormatIssues == 0;
            detail.LayoutFormatIssueCount = layoutFormatIssues;

            if (layoutFormatIssues > 0)
            {
                detail.Warning = loc["VersionCheck.LayoutFormatIssues"].Replace("{count}", layoutFormatIssues.ToString());
            }
        }

        return detail;
    }

    /// <summary>
    /// 检查补丁文件的伴生文件（.gpu_resources / .stream）是否存在。
    /// </summary>
    private static void CheckCompanionFiles(FileInfo patchFile, PatchFileAnalysis analysis)
    {
        var directory = patchFile.Directory;
        if (directory is null)
            return;

        var baseName = patchFile.Name;
        var gpuPath = Path.Combine(directory.FullName, baseName + ".gpu_resources");
        var streamPath = Path.Combine(directory.FullName, baseName + ".stream");

        analysis.HasGpuResources = File.Exists(gpuPath);
        analysis.HasStream = File.Exists(streamPath);
    }

    /// <summary>
    /// 根据主补丁文件条目中记录的 GPU offset/size，检查 .gpu_resources 数据是否越界。
    /// </summary>
    private static void CheckGpuResourceBounds(FileInfo patchFile, PatchFileAnalysis analysis, byte[] data, int fileEntriesOffset, int numFiles)
    {
        var directory = patchFile.Directory;
        if (directory is null)
            return;

        var gpuPath = Path.Combine(directory.FullName, patchFile.Name + ".gpu_resources");
        if (!File.Exists(gpuPath))
            return;

        var gpuSize = new FileInfo(gpuPath).Length;
        int issues = 0;

        for (int i = 0; i < numFiles; i++)
        {
            var entryOffset = fileEntriesOffset + i * 80;
            if (entryOffset + 80 > data.Length)
                break;

            var gpuOffset = MemoryMarshal.Read<ulong>(data.AsSpan(entryOffset + 32, 8));
            var gpuResourceSize = MemoryMarshal.Read<uint>(data.AsSpan(entryOffset + 64, 4));

            if (gpuResourceSize == 0)
                continue;

            if (gpuOffset > (ulong)gpuSize || gpuOffset + gpuResourceSize > (ulong)gpuSize)
                issues++;
        }

        analysis.GpuResourceIssueCount = issues;
        analysis.GpuResourceBoundsValid = issues == 0;

        if (issues > 0 && analysis.HealthStatus == PatchHealthStatus.Healthy)
            analysis.HealthStatus = PatchHealthStatus.Warning;
    }

    private async Task<PatchFileAnalysis> AnalyzeSinglePatchFileStructureStreamAsync(FileInfo patchFile, PatchFileAnalysis analysis)
    {
        await using var stream = OpenPatchReadStream(patchFile);
        var header = new byte[HeaderSize];
        if (!await ReadAtAsync(stream, 0, header))
        {
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.Message = _localizationService["VersionCheck.FileTooSmallToContainHeader"];
            return analysis;
        }

        var magic = MemoryMarshal.Read<int>(header.AsSpan(0, 4));
        if (magic != PatchHeaderMagic)
        {
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.HeaderValid = false;
            analysis.Message = _localizationService["VersionCheck.InvalidMagicNumber"].Replace("{actual}", $"0x{magic:X8}").Replace("{expected}", $"0x{PatchHeaderMagic:X8}");
            return analysis;
        }
        analysis.HeaderValid = true;

        var numTypes = MemoryMarshal.Read<int>(header.AsSpan(4, 4));
        var numFiles = MemoryMarshal.Read<int>(header.AsSpan(8, 4));
        analysis.NumTypes = numTypes;
        analysis.NumFiles = numFiles;

        if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
        {
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.Message = _localizationService["VersionCheck.SuspiciousHeaderValues"].Replace("{numTypes}", numTypes.ToString()).Replace("{numFiles}", numFiles.ToString());
            return analysis;
        }

        var typeEntriesOffset = HeaderSize;
        var fileEntriesOffset = typeEntriesOffset + (long)numTypes * TypeEntrySize;
        var typeEntry = new byte[TypeEntrySize];
        var totalResources = 0;
        var foundUnitType = false;

        for (var i = 0; i < numTypes; i++)
        {
            var teOffset = typeEntriesOffset + (long)i * TypeEntrySize;
            if (!await ReadAtAsync(stream, teOffset, typeEntry))
                break;

            var typeId = MemoryMarshal.Read<long>(typeEntry.AsSpan(8, 8));
            var numResources = MemoryMarshal.Read<long>(typeEntry.AsSpan(24, 8));
            if (numResources > int.MaxValue || totalResources + numResources > int.MaxValue)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.SuspiciousHeaderValues"].Replace("{numTypes}", numTypes.ToString()).Replace("{numFiles}", numFiles.ToString());
                return analysis;
            }

            totalResources += (int)numResources;
            if (typeId == UnitTypeId)
                foundUnitType = true;
        }
        analysis.TotalResources = totalResources;

        if (totalResources < numFiles)
        {
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.Message = _localizationService["VersionCheck.ResourceCountMismatch"].Replace("{totalResources}", totalResources.ToString()).Replace("{numFiles}", numFiles.ToString());
            return analysis;
        }

        if (!foundUnitType)
        {
            analysis.HealthStatus = PatchHealthStatus.NoUnitResources;
            analysis.Message = _localizationService["VersionCheck.NoUnitResourcesInFile"];
            CheckCompanionFiles(patchFile, analysis);
            return analysis;
        }

        if (fileEntriesOffset + (long)numFiles * FileEntrySize > stream.Length)
        {
            analysis.HealthStatus = PatchHealthStatus.Corrupted;
            analysis.FileEntriesInBounds = false;
            analysis.Message = _localizationService["VersionCheck.FileEntriesExceedBounds"];
            return analysis;
        }
        analysis.FileEntriesInBounds = true;

        CheckCompanionFiles(patchFile, analysis);
        await CheckGpuResourceBoundsStreamAsync(patchFile, analysis, stream, fileEntriesOffset, numFiles);

        var unitDetails = new List<UnitResourceDetail>();
        var fileEntry = new byte[FileEntrySize];
        for (var i = 0; i < numFiles; i++)
        {
            var entryOffset = fileEntriesOffset + (long)i * FileEntrySize;
            if (!await ReadAtAsync(stream, entryOffset, fileEntry))
                break;

            var typeId = MemoryMarshal.Read<long>(fileEntry.AsSpan(8, 8));
            if (typeId != UnitTypeId)
                continue;

            var fileId = MemoryMarshal.Read<long>(fileEntry.AsSpan(0, 8));
            var dataOffset = MemoryMarshal.Read<long>(fileEntry.AsSpan(16, 8));
            var dataSize = MemoryMarshal.Read<int>(fileEntry.AsSpan(56, 4));

            if (dataOffset > stream.Length)
            {
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
                analysis.Message = _localizationService["VersionCheck.UnitDataOffsetExceedsFileSize"].Replace("{fileId}", $"0x{fileId:X16}").Replace("{dataOffset}", $"0x{dataOffset:X}");
                break;
            }

            unitDetails.Add(await AnalyzeUnitResourceDeepAsync(patchFile.Name, fileId, stream, dataOffset, dataSize, _localizationService));
        }

        analysis.UnitDetails = unitDetails;
        if (unitDetails.Any(u => !u.LODGroupInBounds || !u.UnitDataInBounds ||
                                 (u.LayoutFormatChecked && !u.LayoutFormatValid)))
        {
            analysis.HealthStatus = PatchHealthStatus.Warning;
        }

        return analysis;
    }

    private static async Task<UnitResourceDetail> AnalyzeUnitResourceDeepAsync(string fileName, long fileId, FileStream stream, long dataOffset, int dataSize, LocalizationService loc)
    {
        var detail = new UnitResourceDetail
        {
            FileName = fileName,
            FileId = fileId,
            Version = 0,
            DataSize = dataSize,
            UnitDataInBounds = dataOffset >= 0 && dataSize >= 0x30 && dataOffset + 0x30 <= stream.Length
        };

        if (!detail.UnitDataInBounds)
            return detail;

        var intBuffer = new byte[4];
        if (!await ReadAtAsync(stream, dataOffset + 0x2C, intBuffer))
            return detail;

        var version = MemoryMarshal.Read<uint>(intBuffer);
        detail.Version = version;

        if (!await ReadAtAsync(stream, dataOffset + 0x30, intBuffer))
            return detail;
        var lodGroupOffset = MemoryMarshal.Read<int>(intBuffer);

        if (!await ReadAtAsync(stream, dataOffset + 0x34, intBuffer))
            return detail;
        var jointListOffset = MemoryMarshal.Read<int>(intBuffer);

        detail.LODGroupOffset = lodGroupOffset;
        detail.JointListOffset = jointListOffset;

        var lodGroupSize = jointListOffset - lodGroupOffset;
        detail.LODGroupSize = lodGroupSize;

        if (lodGroupOffset >= 0 && lodGroupSize > 0 && dataOffset + lodGroupOffset + lodGroupSize <= stream.Length)
            detail.LODGroupInBounds = true;

        if (version < VersionThresholdForLayoutCheck)
        {
            detail.LayoutFormatChecked = true;
            var layoutFormatIssues = 0;

            if (dataSize >= 0x60 && await ReadAtAsync(stream, dataOffset + 0x5C, intBuffer))
            {
                var layoutListOffset = MemoryMarshal.Read<int>(intBuffer);
                var numLayoutsOffset = dataOffset + layoutListOffset;
                if (numLayoutsOffset + 4 <= stream.Length && await ReadAtAsync(stream, numLayoutsOffset, intBuffer))
                {
                    var numLayouts = MemoryMarshal.Read<int>(intBuffer);
                    var layoutArrayOffset = numLayoutsOffset + 4;

                    if (numLayouts > 0 && numLayouts <= 100 && layoutArrayOffset + (long)numLayouts * 4 <= stream.Length)
                    {
                        for (var li = 0; li < numLayouts; li++)
                        {
                            if (!await ReadAtAsync(stream, layoutArrayOffset + (long)li * 4, intBuffer))
                                continue;

                            var layoutOffset = MemoryMarshal.Read<int>(intBuffer);
                            var layoutStart = dataOffset + layoutListOffset + layoutOffset;
                            if (layoutStart + 8 + 16 * 20 > stream.Length)
                                continue;

                            for (var item = 0; item < 16; item++)
                            {
                                var itemOffset = layoutStart + 8 + item * 20;
                                if (!await ReadAtAsync(stream, itemOffset + 4, intBuffer))
                                    continue;

                                var itemFormat = MemoryMarshal.Read<int>(intBuffer);
                                if (itemFormat > 16)
                                    layoutFormatIssues++;
                            }
                        }
                    }
                }
            }

            detail.LayoutFormatValid = layoutFormatIssues == 0;
            detail.LayoutFormatIssueCount = layoutFormatIssues;

            if (layoutFormatIssues > 0)
                detail.Warning = loc["VersionCheck.LayoutFormatIssues"].Replace("{count}", layoutFormatIssues.ToString());
        }

        return detail;
    }

    private static async Task CheckGpuResourceBoundsStreamAsync(FileInfo patchFile, PatchFileAnalysis analysis, FileStream stream, long fileEntriesOffset, int numFiles)
    {
        var directory = patchFile.Directory;
        if (directory is null)
            return;

        var gpuPath = Path.Combine(directory.FullName, patchFile.Name + ".gpu_resources");
        if (!File.Exists(gpuPath))
            return;

        var gpuSize = new FileInfo(gpuPath).Length;
        var issues = 0;
        var entry = new byte[FileEntrySize];

        for (var i = 0; i < numFiles; i++)
        {
            var entryOffset = fileEntriesOffset + (long)i * FileEntrySize;
            if (!await ReadAtAsync(stream, entryOffset, entry))
                break;

            var gpuOffset = MemoryMarshal.Read<ulong>(entry.AsSpan(32, 8));
            var gpuResourceSize = MemoryMarshal.Read<uint>(entry.AsSpan(64, 4));

            if (gpuResourceSize == 0)
                continue;

            if (gpuOffset > (ulong)gpuSize || gpuOffset + gpuResourceSize > (ulong)gpuSize)
                issues++;
        }

        analysis.GpuResourceIssueCount = issues;
        analysis.GpuResourceBoundsValid = issues == 0;

        if (issues > 0 && analysis.HealthStatus == PatchHealthStatus.Healthy)
            analysis.HealthStatus = PatchHealthStatus.Warning;
    }

    private static bool ShouldUseMemoryRead(FileInfo file)
    {
        if (file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase))
            return false;

        var memoryInfo = GC.GetGCMemoryInfo();
        var availableMemory = memoryInfo.TotalAvailableMemoryBytes - GC.GetTotalMemory(false);
        if (availableMemory <= 0)
            return false;

        var safeReadLimit = Math.Min(availableMemory / 10, MaxMemoryReadBytes);
        return file.Length > 0 && file.Length <= safeReadLimit && file.Length <= int.MaxValue;
    }

    private static FileStream OpenPatchReadStream(FileInfo file)
    {
        return new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.RandomAccess);
    }

    private static async Task<bool> ReadAtAsync(FileStream stream, long offset, byte[] buffer)
    {
        if (offset < 0 || offset + buffer.Length > stream.Length)
            return false;

        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (count == 0)
                return false;
            read += count;
        }

        return true;
    }
}
