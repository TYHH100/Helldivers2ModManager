using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 版本检测服务。
/// Patch/Unit 结构研究、旧 Layout 版本处理和校验策略参考
/// hd2-repatcher（https://github.com/RaidingForPants/hd2-repatcher）；
/// 补丁/归档格式常量参考 HD2SDK-CommunityEdition
/// （https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition）。
/// 以多数版本作为参考基准，标记偏离的模组。
/// v1.5.0 新增深度分析：文件结构完整性校验、Unit 内部结构分析、伴生文件检查。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed partial class VersionCheckService
{
    /// <summary>
    /// Unit 资源类型 ID（参考 hd2-repatcher 的补丁结构研究）。
    /// </summary>
    private const long UnitTypeId = unchecked((long)16187218042980615487UL);

    /// <summary>
    /// 补丁文件头魔数（0xF0000011），参考 HD2SDK-CommunityEdition 的格式资料。
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
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;

    public VersionCheckService(ILogger<VersionCheckService> logger, SettingsService settingsService, LocalizationService localizationService)
    {
        _logger = logger;
        _settingsService = settingsService;
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

        _logger.LogInformation("开始扫描 {Count} 个模组的补丁结构和 Unit 版本...", modList.Count);
        var allModScans = new ConcurrentDictionary<Guid, (List<uint> Versions, List<PatchUnitInfo> Infos, ModDetailedAnalysis Analysis)>();
        using var semaphore = new SemaphoreSlim(2, 2);

        var scanTasks = modList.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                var analysis = await AnalyzeModPatchFilesAsync(mod.Directory);
                var infos = analysis.PatchFiles
                    .SelectMany(p => p.UnitDetails)
                    .Select(ToPatchUnitInfo)
                    .ToList();
                allModScans[mod.Manifest.Guid] = (infos.Select(i => i.Version).ToList(), infos, analysis);
            }
            finally
            {
                semaphore.Release();
            }
        });
        await Task.WhenAll(scanTasks);

        // 结构已损坏的补丁不能参与参考版本投票，否则大量旧坏包会反向污染基准。
        var allVersions = allModScans.Values
            .Where(v => !HasBlockingStructuralIssues(v.Analysis))
            .SelectMany(v => v.Versions)
            .ToList();
        uint? referenceVersion = allVersions.Count > 0 ? GetMostCommonVersion(allVersions) : null;
        s_cachedReferenceVersion = referenceVersion;
        s_cachedModCount = allModScans.Values.Count(v => v.Versions.Count > 0);
        s_cachedUnitCount = allVersions.Count;

        if (referenceVersion.HasValue)
        {
            _logger.LogInformation("Reference Unit version: 0x{Version:X8} (from {UnitCount} Unit entries across {ModCount} mods)",
                referenceVersion.Value, s_cachedUnitCount, s_cachedModCount);
        }

        foreach (var mod in modList)
        {
            var scan = allModScans[mod.Manifest.Guid];
            var hasBlockingIssues = HasBlockingStructuralIssues(scan.Analysis);
            var status = hasBlockingIssues
                ? ModVersionStatus.Incompatible
                : scan.Versions.Count == 0 || !referenceVersion.HasValue
                    ? ModVersionStatus.Unknown
                    : scan.Versions.All(v => v == referenceVersion.Value)
                        ? ModVersionStatus.Compatible
                        : ModVersionStatus.Incompatible;

            results[mod.Manifest.Guid] = new ModVersionCheckResult
            {
                Status = status,
                GameVersion = referenceVersion ?? 0,
                LastChecked = DateTime.Now,
                PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(scan.Infos)
            };
        }

        _logger.LogInformation("版本和结构检查完成: {Total} 个模组", results.Count);
        return results.ToDictionary(k => k.Key, v => v.Value);
    }

    /// <summary>
    /// 对单个新增或变动模组执行版本与结构检测。
    /// </summary>
    public async Task<ModVersionCheckResult?> CheckSingleModAsync(ModData mod, uint? fallbackVersion = null, bool includeDetailedAnalysis = false)
    {
        var referenceVersion = s_cachedReferenceVersion ?? fallbackVersion;
        var analysis = await AnalyzeModPatchFilesAsync(mod.Directory);
        var infos = analysis.PatchFiles
            .SelectMany(p => p.UnitDetails)
            .Select(ToPatchUnitInfo)
            .ToList();
        var versions = infos.Select(i => i.Version).ToList();
        var hasBlockingIssues = HasBlockingStructuralIssues(analysis);

        ModVersionStatus status;
        uint reportedVersion;
        if (hasBlockingIssues)
        {
            status = ModVersionStatus.Incompatible;
            reportedVersion = referenceVersion ?? (versions.Count > 0 ? GetMostCommonVersion(versions) : 0);
        }
        else if (versions.Count == 0)
        {
            status = ModVersionStatus.Unknown;
            reportedVersion = referenceVersion ?? 0;
        }
        else if (referenceVersion.HasValue)
        {
            status = versions.All(v => v == referenceVersion.Value)
                ? ModVersionStatus.Compatible
                : ModVersionStatus.Incompatible;
            reportedVersion = referenceVersion.Value;
        }
        else
        {
            status = ModVersionStatus.Compatible;
            reportedVersion = GetMostCommonVersion(versions);
        }

        _logger.LogInformation(
            "Mod {Name} compatibility check: {Status}, patches={PatchCount}, corrupted={CorruptedCount}",
            mod.Manifest.Name, status, analysis.TotalPatchFiles, analysis.CorruptedFileCount);

        return new ModVersionCheckResult
        {
            Status = status,
            GameVersion = reportedVersion,
            LastChecked = DateTime.Now,
            PatchUnits = new System.Collections.ObjectModel.ObservableCollection<PatchUnitInfo>(infos),
            DetailedAnalysis = includeDetailedAnalysis ? analysis : null
        };
    }

    private static PatchUnitInfo ToPatchUnitInfo(UnitResourceDetail detail)
    {
        return new PatchUnitInfo
        {
            FileName = detail.FileName,
            FileId = detail.FileId,
            Version = detail.Version,
            DataSize = detail.DataSize
        };
    }

    private static bool HasBlockingStructuralIssues(ModDetailedAnalysis analysis)
    {
        return analysis.CorruptedFileCount > 0 ||
               analysis.HasCompanionFileIssues ||
               analysis.PatchFiles.Any(p =>
                   !p.GpuResourceBoundsValid || !p.StreamBoundsValid ||
                   p.UnitDetails.Any(u => !u.UnitDataInBounds || !u.LODGroupInBounds || u.IsTruncated ||
                                          (u.LayoutFormatChecked && !u.LayoutFormatValid)));
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

    /// <summary>
    /// 从当前游戏 bundle 参考中解析 Unit 对应的显示名。
    /// 这里不依赖仓库内的静态文本映射。
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string>> ResolveGameUnitDisplayNamesAsync(
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken = default)
    {
        if (unitIds.Count == 0)
            return new Dictionary<long, string>();

        var lookup = await GetGameUnitReferencesAsync(unitIds);
        var result = new Dictionary<long, string>();
        foreach (var (unitId, reference) in lookup.References)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displayName = NormalizeGamePackageName(reference.PackageName);
            if (!string.IsNullOrWhiteSpace(displayName))
                result[unitId] = displayName;
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
    // 解析完整 Stingray legacy package TOC，并验证三路资源边界。
    // ===================================================================

    private readonly record struct PatchTocEntry(
        long FileId,
        long TypeId,
        ulong TocOffset,
        ulong StreamOffset,
        ulong GpuOffset,
        uint TocSize,
        uint StreamSize,
        uint GpuSize,
        uint EntryIndex);

    /// <summary>
    /// 对单个模组的所有补丁文件执行深度分析。
    /// </summary>
    private async Task<ModDetailedAnalysis> AnalyzeModPatchFilesAsync(DirectoryInfo modDir)
    {
        var patchFiles = modDir.GetFiles("*", SearchOption.AllDirectories)
            .Where(f => IsMainPatchFile(f.Name))
            .ToArray();

        var analysis = new ModDetailedAnalysis { TotalPatchFiles = patchFiles.Length };
        if (patchFiles.Length == 0)
            return analysis;

        var patchAnalyses = new List<PatchFileAnalysis>(patchFiles.Length);
        var typeDistributions = new Dictionary<long, int>();

        foreach (var patchFile in patchFiles)
        {
            var patchAnalysis = await AnalyzeSinglePatchFileStructureAsync(patchFile);
            patchAnalyses.Add(patchAnalysis);

            foreach (var resourceType in patchAnalysis.ResourceTypes)
            {
                typeDistributions.TryGetValue(resourceType.TypeId, out var current);
                typeDistributions[resourceType.TypeId] = current + resourceType.ResourceCount;
            }
        }

        analysis.PatchFiles = patchAnalyses;
        analysis.FilesWithUnits = patchAnalyses.Count(p => p.UnitDetails.Count > 0);
        analysis.HealthyFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Healthy);
        analysis.WarningFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Warning);
        analysis.CorruptedFileCount = patchAnalyses.Count(p => p.HealthStatus == PatchHealthStatus.Corrupted);
        analysis.HasStructuralIssues = patchAnalyses.Any(p =>
            !p.HeaderValid || !p.FileEntriesInBounds || !p.TypeDistributionValid || !p.MainDataBoundsValid ||
            !p.EntryIndicesValid || p.HealthStatus == PatchHealthStatus.Corrupted);
        analysis.HasCompanionFileIssues = patchAnalyses.Any(p =>
            (p.RequiresGpuResources && !p.HasGpuResources) ||
            (p.RequiresStream && !p.HasStream));
        analysis.HasUnitStructuralIssues = patchAnalyses.Any(p => p.UnitDetails.Any(u =>
            !u.LODGroupInBounds || !u.UnitDataInBounds || !u.DeclaredSizeMatchesInternal ||
            (u.LayoutFormatChecked && !u.LayoutFormatValid)));
        analysis.HasGpuResourceIssues = patchAnalyses.Any(p =>
            !p.GpuResourceBoundsValid || p.GpuAlignmentIssueCount > 0);
        analysis.HasStreamResourceIssues = patchAnalyses.Any(p =>
            !p.StreamBoundsValid || p.StreamAlignmentIssueCount > 0);
        analysis.ResourceTypes = typeDistributions
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ResourceTypeDistribution { TypeId = kv.Key, ResourceCount = kv.Value })
            .ToList();

        return analysis;
    }

    private static bool IsMainPatchFile(string name)
    {
        return name.Contains(".patch_", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(".hd2mm-repair-", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains(".hd2mm-backup", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 流式解析补丁；不会把大型主文件或伴生 GPU 文件整体加载进内存。
    /// </summary>
    private async Task<PatchFileAnalysis> AnalyzeSinglePatchFileStructureAsync(FileInfo patchFile, FileInfo? companionSource = null)
    {
        var analysis = new PatchFileAnalysis
        {
            FileName = patchFile.Name,
            FileSize = patchFile.Exists ? patchFile.Length : 0,
            HealthStatus = PatchHealthStatus.Healthy
        };

        try
        {
            if (!patchFile.Exists || patchFile.Length < HeaderSize)
            {
                MarkCorrupted(analysis, _localizationService["VersionCheck.FileTooSmallOrMissing"]);
                return analysis;
            }

            await using var stream = OpenPatchReadStream(patchFile);
            var header = new byte[HeaderSize];
            if (!await ReadAtAsync(stream, 0, header))
            {
                MarkCorrupted(analysis, _localizationService["VersionCheck.FileTooSmallToContainHeader"]);
                return analysis;
            }

            var magic = MemoryMarshal.Read<int>(header.AsSpan(0, 4));
            if (magic != PatchHeaderMagic)
            {
                analysis.HeaderValid = false;
                MarkCorrupted(analysis, _localizationService["VersionCheck.InvalidMagicNumber"]
                    .Replace("{actual}", $"0x{magic:X8}")
                    .Replace("{expected}", $"0x{PatchHeaderMagic:X8}"));
                return analysis;
            }

            analysis.HeaderValid = true;
            var numTypes = MemoryMarshal.Read<int>(header.AsSpan(4, 4));
            var numFiles = MemoryMarshal.Read<int>(header.AsSpan(8, 4));
            analysis.NumTypes = numTypes;
            analysis.NumFiles = numFiles;

            if (numTypes < 0 || numFiles < 0 || numTypes > 1000 || numFiles > 100000)
            {
                MarkCorrupted(analysis, _localizationService["VersionCheck.SuspiciousHeaderValues"]
                    .Replace("{numTypes}", numTypes.ToString())
                    .Replace("{numFiles}", numFiles.ToString()));
                return analysis;
            }

            var typeEntriesLength = checked(numTypes * TypeEntrySize);
            var fileEntriesOffset = HeaderSize + (long)typeEntriesLength;
            var fileEntriesLength = checked((long)numFiles * FileEntrySize);
            if (fileEntriesOffset + fileEntriesLength > stream.Length)
            {
                analysis.FileEntriesInBounds = false;
                MarkCorrupted(analysis, _localizationService["VersionCheck.FileEntriesExceedBounds"]);
                return analysis;
            }

            var declaredTypeCounts = new Dictionary<long, int>();
            ulong totalResources = 0;
            var typeData = new byte[typeEntriesLength];
            if (typeData.Length > 0 && !await ReadAtAsync(stream, HeaderSize, typeData))
            {
                MarkCorrupted(analysis, _localizationService["VersionCheck.FileEntriesExceedBounds"]);
                return analysis;
            }

            for (var i = 0; i < numTypes; i++)
            {
                var offset = i * TypeEntrySize;
                var typeId = MemoryMarshal.Read<long>(typeData.AsSpan(offset + 8, 8));
                var resourceCount = MemoryMarshal.Read<ulong>(typeData.AsSpan(offset + 16, 8));
                if (resourceCount > int.MaxValue)
                {
                    analysis.TypeDistributionValid = false;
                    analysis.TypeDistributionIssueCount++;
                    MarkCorrupted(analysis, _localizationService["VersionCheck.InvalidTypeTable"]);
                }
                else if (!declaredTypeCounts.TryAdd(typeId, (int)resourceCount))
                {
                    analysis.TypeDistributionValid = false;
                    analysis.TypeDistributionIssueCount++;
                    MarkCorrupted(analysis, _localizationService["VersionCheck.InvalidTypeTable"]);
                }
                totalResources += resourceCount;
            }

            analysis.TotalResources = totalResources <= int.MaxValue ? (int)totalResources : int.MaxValue;
            analysis.ResourceTypes = declaredTypeCounts
                .Select(kv => new ResourceTypeDistribution { TypeId = kv.Key, ResourceCount = kv.Value })
                .ToList();

            if (totalResources != (ulong)numFiles)
            {
                analysis.TypeDistributionValid = false;
                analysis.TypeDistributionIssueCount++;
                MarkCorrupted(analysis, _localizationService["VersionCheck.ResourceCountMismatch"]
                    .Replace("{totalResources}", totalResources.ToString())
                    .Replace("{numFiles}", numFiles.ToString()));
            }

            var entries = new List<PatchTocEntry>(numFiles);
            var actualTypeCounts = new Dictionary<long, int>();
            var entryBuffer = new byte[FileEntrySize];
            for (var i = 0; i < numFiles; i++)
            {
                if (!await ReadAtAsync(stream, fileEntriesOffset + (long)i * FileEntrySize, entryBuffer))
                {
                    analysis.FileEntriesInBounds = false;
                    MarkCorrupted(analysis, _localizationService["VersionCheck.FileEntriesExceedBounds"]);
                    return analysis;
                }

                var entry = new PatchTocEntry(
                    MemoryMarshal.Read<long>(entryBuffer.AsSpan(0, 8)),
                    MemoryMarshal.Read<long>(entryBuffer.AsSpan(8, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(16, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(24, 8)),
                    MemoryMarshal.Read<ulong>(entryBuffer.AsSpan(32, 8)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(56, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(60, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(64, 4)),
                    MemoryMarshal.Read<uint>(entryBuffer.AsSpan(76, 4)));
                entries.Add(entry);
                actualTypeCounts.TryGetValue(entry.TypeId, out var count);
                actualTypeCounts[entry.TypeId] = count + 1;

                if (entry.EntryIndex != (uint)(i + 1))
                    analysis.EntryIndexIssueCount++;
            }

            analysis.FileEntriesInBounds = true;
            analysis.EntryIndicesValid = analysis.EntryIndexIssueCount == 0;
            if (!analysis.EntryIndicesValid)
                MarkWarning(analysis, _localizationService["VersionCheck.EntryIndexMismatch"]
                    .Replace("{count}", analysis.EntryIndexIssueCount.ToString()));

            if (declaredTypeCounts.Count != actualTypeCounts.Count ||
                declaredTypeCounts.Any(kv => !actualTypeCounts.TryGetValue(kv.Key, out var count) || count != kv.Value))
            {
                analysis.TypeDistributionValid = false;
                analysis.TypeDistributionIssueCount++;
                MarkCorrupted(analysis, _localizationService["VersionCheck.TypeDistributionMismatch"]);
            }

            ValidateMainDataRanges(entries, stream.Length, fileEntriesOffset + fileEntriesLength, analysis);
            ValidateCompanionFiles(companionSource ?? patchFile, entries, analysis);

            var unitDetails = new List<UnitResourceDetail>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.TypeId != UnitTypeId)
                    continue;

                var detail = await AnalyzeUnitResourceDeepAsync(
                    patchFile.Name, i + 1, entry, stream, _localizationService);
                unitDetails.Add(detail);
            }
            analysis.UnitDetails = unitDetails;

            if (unitDetails.Any(u => !u.UnitDataInBounds || !u.LODGroupInBounds || u.IsTruncated))
                analysis.HealthStatus = PatchHealthStatus.Corrupted;
            else if (unitDetails.Any(u => !u.DeclaredSizeMatchesInternal ||
                                         (u.LayoutFormatChecked && !u.LayoutFormatValid)))
                MarkWarning(analysis, null);

            if (unitDetails.Count == 0 && analysis.HealthStatus == PatchHealthStatus.Healthy)
            {
                analysis.HealthStatus = PatchHealthStatus.NoUnitResources;
                AddAnalysisMessage(analysis, _localizationService["VersionCheck.NoUnitResourcesInFile"]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分析补丁文件 {File} 结构时出错", patchFile.Name);
            MarkCorrupted(analysis, _localizationService["VersionCheck.AnalysisException"]
                .Replace("{message}", ex.Message));
        }

        return analysis;
    }

    private static void ValidateMainDataRanges(
        IReadOnlyList<PatchTocEntry> entries,
        long fileLength,
        long minimumDataOffset,
        PatchFileAnalysis analysis)
    {
        var ranges = new List<(ulong Start, ulong End)>();
        foreach (var entry in entries)
        {
            if (!IsRangeInBounds(entry.TocOffset, entry.TocSize, fileLength) ||
                (entry.TocSize > 0 && entry.TocOffset < (ulong)minimumDataOffset))
            {
                analysis.MainDataIssueCount++;
                continue;
            }

            if (entry.TocSize > 0)
                ranges.Add((entry.TocOffset, entry.TocOffset + entry.TocSize));
        }

        ranges.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var i = 1; i < ranges.Count; i++)
        {
            if (ranges[i].Start < ranges[i - 1].End)
                analysis.MainDataIssueCount++;
        }

        analysis.MainDataBoundsValid = analysis.MainDataIssueCount == 0;
        if (!analysis.MainDataBoundsValid)
            MarkCorrupted(analysis, null);
    }

    private static void ValidateCompanionFiles(
        FileInfo patchFile,
        IReadOnlyList<PatchTocEntry> entries,
        PatchFileAnalysis analysis)
    {
        var directory = patchFile.Directory;
        if (directory is null)
            return;

        var gpuPath = Path.Combine(directory.FullName, patchFile.Name + ".gpu_resources");
        var streamPath = Path.Combine(directory.FullName, patchFile.Name + ".stream");
        analysis.HasGpuResources = File.Exists(gpuPath);
        analysis.HasStream = File.Exists(streamPath);
        analysis.RequiresGpuResources = entries.Any(e => e.GpuSize > 0);
        analysis.RequiresStream = entries.Any(e => e.StreamSize > 0);

        var gpuLength = analysis.HasGpuResources ? new FileInfo(gpuPath).Length : 0;
        var streamLength = analysis.HasStream ? new FileInfo(streamPath).Length : 0;

        foreach (var entry in entries)
        {
            if (entry.GpuSize > 0)
            {
                if (!analysis.HasGpuResources || !IsRangeInBounds(entry.GpuOffset, entry.GpuSize, gpuLength))
                    analysis.GpuResourceIssueCount++;
                if (entry.GpuOffset % 64 != 0)
                    analysis.GpuAlignmentIssueCount++;
            }

            if (entry.StreamSize > 0)
            {
                if (!analysis.HasStream || !IsRangeInBounds(entry.StreamOffset, entry.StreamSize, streamLength))
                    analysis.StreamIssueCount++;
                if (entry.StreamOffset % 64 != 0)
                    analysis.StreamAlignmentIssueCount++;
            }
        }

        analysis.GpuResourceBoundsValid = analysis.GpuResourceIssueCount == 0;
        analysis.StreamBoundsValid = analysis.StreamIssueCount == 0;
        if (!analysis.GpuResourceBoundsValid || !analysis.StreamBoundsValid)
            MarkCorrupted(analysis, null);
        else if (analysis.GpuAlignmentIssueCount > 0 || analysis.StreamAlignmentIssueCount > 0)
            MarkWarning(analysis, null);
    }

    private static async Task<UnitResourceDetail> AnalyzeUnitResourceDeepAsync(
        string fileName,
        int entryIndex,
        PatchTocEntry entry,
        FileStream stream,
        LocalizationService loc)
    {
        var detail = new UnitResourceDetail
        {
            FileName = fileName,
            EntryIndex = entryIndex,
            FileId = entry.FileId,
            DataSize = entry.TocSize <= int.MaxValue ? (int)entry.TocSize : int.MaxValue,
            UnitDataInBounds = IsRangeInBounds(entry.TocOffset, entry.TocSize, stream.Length) && entry.TocSize >= 0x68
        };

        if (!detail.UnitDataInBounds)
        {
            detail.Warning = loc["VersionCheck.UnitDataOutOfBounds"];
            return detail;
        }

        var unitHeader = new byte[0x68];
        if (!await ReadAtAsync(stream, (long)entry.TocOffset, unitHeader))
        {
            detail.UnitDataInBounds = false;
            detail.Warning = loc["VersionCheck.UnitDataOutOfBounds"];
            return detail;
        }

        detail.Version = MemoryMarshal.Read<uint>(unitHeader.AsSpan(0x2C, 4));
        detail.LODGroupOffset = MemoryMarshal.Read<int>(unitHeader.AsSpan(0x30, 4));
        detail.JointListOffset = MemoryMarshal.Read<int>(unitHeader.AsSpan(0x34, 4));
        detail.EndingOffset = MemoryMarshal.Read<int>(unitHeader.AsSpan(0x60, 4));

        if (detail.EndingOffset > 0 && detail.EndingOffset <= int.MaxValue - 8)
        {
            detail.ExpectedDataSize = detail.EndingOffset + 8;
            detail.DeclaredSizeMatchesInternal = detail.ExpectedDataSize == detail.DataSize;
            detail.IsTruncated = detail.ExpectedDataSize > detail.DataSize;
            if (!detail.DeclaredSizeMatchesInternal)
            {
                detail.Warning = loc[detail.IsTruncated
                        ? "VersionCheck.UnitDataSizeTruncated"
                        : "VersionCheck.UnitDataSizeMismatch"]
                    .Replace("{declared}", detail.DataSize.ToString())
                    .Replace("{expected}", detail.ExpectedDataSize.ToString())
                    .Replace("{difference}", Math.Abs(detail.ExpectedDataSize - detail.DataSize).ToString());
            }
        }

        detail.LODGroupSize = detail.JointListOffset - detail.LODGroupOffset;
        detail.LODGroupInBounds =
            (detail.LODGroupOffset == 0 && detail.JointListOffset == 0) ||
            (detail.LODGroupOffset >= 0 && detail.LODGroupSize > 0 &&
             (long)detail.LODGroupOffset + detail.LODGroupSize <= entry.TocSize);
        if (!detail.LODGroupInBounds)
            AppendUnitWarning(detail, loc["VersionCheck.LodDataOutOfBounds"]);

        if (detail.Version < VersionThresholdForLayoutCheck)
            await AnalyzeLegacyLayoutAsync(detail, entry, stream, unitHeader, loc);

        return detail;
    }

    private static async Task AnalyzeLegacyLayoutAsync(
        UnitResourceDetail detail,
        PatchTocEntry entry,
        FileStream stream,
        byte[] unitHeader,
        LocalizationService loc)
    {
        detail.LayoutFormatChecked = true;
        detail.LayoutFormatValid = true;
        var layoutListOffset = MemoryMarshal.Read<int>(unitHeader.AsSpan(0x5C, 4));
        if (layoutListOffset < 0 || (long)layoutListOffset + 4 > entry.TocSize)
        {
            detail.LayoutFormatValid = false;
            detail.LayoutFormatIssueCount = 1;
            AppendUnitWarning(detail, loc["VersionCheck.LayoutDataOutOfBounds"]);
            return;
        }

        var countBuffer = new byte[4];
        if (!await ReadAtAsync(stream, (long)entry.TocOffset + layoutListOffset, countBuffer))
        {
            detail.LayoutFormatValid = false;
            detail.LayoutFormatIssueCount = 1;
            AppendUnitWarning(detail, loc["VersionCheck.LayoutDataOutOfBounds"]);
            return;
        }

        var numLayouts = MemoryMarshal.Read<int>(countBuffer);
        if (numLayouts < 0 || numLayouts > 100 || (long)layoutListOffset + 4L + numLayouts * 4L > entry.TocSize)
        {
            detail.LayoutFormatValid = false;
            detail.LayoutFormatIssueCount = 1;
            AppendUnitWarning(detail, loc["VersionCheck.LayoutDataOutOfBounds"]);
            return;
        }

        var offsetsBuffer = new byte[numLayouts * 4];
        if (offsetsBuffer.Length > 0 &&
            !await ReadAtAsync(stream, (long)entry.TocOffset + layoutListOffset + 4, offsetsBuffer))
        {
            detail.LayoutFormatValid = false;
            detail.LayoutFormatIssueCount = 1;
            AppendUnitWarning(detail, loc["VersionCheck.LayoutDataOutOfBounds"]);
            return;
        }

        var layoutBuffer = new byte[8 + 16 * 20];
        for (var i = 0; i < numLayouts; i++)
        {
            var relativeOffset = MemoryMarshal.Read<int>(offsetsBuffer.AsSpan(i * 4, 4));
            var layoutStart = (long)layoutListOffset + relativeOffset;
            if (relativeOffset < 0 || layoutStart < 0 || layoutStart + layoutBuffer.Length > entry.TocSize ||
                !await ReadAtAsync(stream, (long)entry.TocOffset + layoutStart, layoutBuffer))
            {
                detail.LayoutFormatValid = false;
                detail.LayoutFormatIssueCount++;
                continue;
            }

            for (var item = 0; item < 16; item++)
            {
                var itemFormat = MemoryMarshal.Read<int>(layoutBuffer.AsSpan(8 + item * 20 + 4, 4));
                if (itemFormat > 16)
                    detail.LayoutFormatIssueCount++;
            }
        }

        detail.LayoutFormatValid = detail.LayoutFormatIssueCount == 0;
        if (!detail.LayoutFormatValid)
            AppendUnitWarning(detail, loc["VersionCheck.LayoutFormatIssues"]
                .Replace("{count}", detail.LayoutFormatIssueCount.ToString()));
    }

    private static bool IsRangeInBounds(ulong offset, uint size, long fileLength)
    {
        if (fileLength < 0 || offset > (ulong)fileLength)
            return false;
        return size <= (ulong)fileLength - offset;
    }

    private static void AppendUnitWarning(UnitResourceDetail detail, string warning)
    {
        detail.Warning = string.IsNullOrWhiteSpace(detail.Warning)
            ? warning
            : detail.Warning + Environment.NewLine + warning;
    }

    private static void MarkCorrupted(PatchFileAnalysis analysis, string? message)
    {
        analysis.HealthStatus = PatchHealthStatus.Corrupted;
        AddAnalysisMessage(analysis, message);
    }

    private static void MarkWarning(PatchFileAnalysis analysis, string? message)
    {
        if (analysis.HealthStatus == PatchHealthStatus.Healthy)
            analysis.HealthStatus = PatchHealthStatus.Warning;
        AddAnalysisMessage(analysis, message);
    }

    private static void AddAnalysisMessage(PatchFileAnalysis analysis, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        analysis.Message = string.IsNullOrWhiteSpace(analysis.Message)
            ? message
            : analysis.Message + Environment.NewLine + message;
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

    private static string NormalizeGamePackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return string.Empty;

        var fileName = Path.GetFileNameWithoutExtension(packageName);
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return packageName.Trim();
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
