using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 一键换甲核心服务：把模组 A 中某个护甲外观组的 body/helmet 网格移植到游戏护甲 B。
/// 产物 = 新模组（patch 三件套 + manifest），B 的 Unit 属性头部保留、网格结构来自 A，
/// 不换 ID、不加 swapid（两者分别导致属性 bug 与第一人称 bug）。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ArmorSwapService
{
    private const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;
    private const ulong MaterialTypeId = 0xEAC0B497876ADEDFUL;
    private const ulong TextureTypeId = 0xCD4238C6A0C69E32UL;
    /// <summary>产物 Unit 主数据中覆盖为目标 B 的头部字节数（属性/版本区）。</summary>
    private const int TargetHeaderBytes = 0x30;

    private readonly ILogger<ArmorSwapService> _logger;
    private readonly ModService _modService;
    private readonly VersionCheckService _versionCheckService;
    private readonly PatchResourceInspectionService _inspectionService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _armorNames;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _helmetNames;

    public ArmorSwapService(
        ILogger<ArmorSwapService> logger,
        ModService modService,
        VersionCheckService versionCheckService,
        PatchResourceInspectionService inspectionService,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _modService = modService;
        _versionCheckService = versionCheckService;
        _inspectionService = inspectionService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _armorNames = new Lazy<IReadOnlyDictionary<string, string>>(LoadArmorNames);
        _helmetNames = new Lazy<IReadOnlyDictionary<string, string>>(LoadHelmetNames);
    }

    /// <summary>
    /// 护甲目录：package ID（16 位 hex）→ 护甲名。同名变体包（如 B-01 的 4 个
    /// Variation，游戏运行时随机选用）折叠为单条代表条目——换甲目标骨架按
    /// 规范化名聚合全部变体包，选哪个代表结果一致。
    /// </summary>
    public IReadOnlyDictionary<string, string> GetArmorCatalog()
    {
        var catalog = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _armorNames.Value.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;
            if (!seenNames.Add(NormalizeArmorName(pair.Value)))
                continue;
            catalog[pair.Key] = pair.Value;
        }
        return catalog;
    }

    /// <summary>来源侧读取单个资源窗口的大小上限（与 InspectionService 一致）。</summary>
    private const long MaxEntryPayloadBytes = 128L * 1024 * 1024;

    /// <summary>
    /// 分析来源模组 A：展开全部选中选项的 patch，按 FileId 合并（部署顺序后覆盖先），
    /// 解析 Unit 结构并按护甲归属分组；同时枚举每个含 patch 的目录（选项/子选项目录）
    /// 的视图，供产物模组保留来源的选项结构。多护甲替换模组会得到多个护甲组。
    /// </summary>
    public async Task<ArmorSwapSourceAnalysis> AnalyzeSourceModAsync(
        ModData mod,
        CancellationToken cancellationToken = default)
    {
        var patchFiles = _modService.GetSelectedPatchFiles(mod);
        var merged = await BuildMergedViewsAsync(patchFiles, cancellationToken);
        var units = merged.Units;
        var materialEntries = merged.MaterialEntries;
        var textureEntries = merged.TextureEntries;

        var warnings = new List<string>();
        if (merged.UnitEntryCount > units.Count)
            warnings.Add(_localizationService["ArmorSwapPage.WarningUnreadableUnits"]
                .Replace("{count}", (merged.UnitEntryCount - units.Count).ToString()));

        // 护甲归属：Unit → 游戏 package 名 → 护甲白名单（名称非空才算有名护甲）
        var unitIds = units.Select(static unit => unit.FileId).Distinct().ToArray();
        var packageNames = await _versionCheckService.ResolveGameUnitPackageNamesAsync(unitIds, cancellationToken);
        var groups = new Dictionary<string, List<ArmorSwapUnitStructure>>(StringComparer.OrdinalIgnoreCase);
        var unnamedUnits = new List<ArmorSwapUnitStructure>();
        var unassignedCount = 0;
        foreach (var unit in units)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!packageNames.TryGetValue(unit.FileId, out var packages))
            {
                unassignedCount++;
                continue;
            }
            // 头盔包归属标记：老 SDK 模组的头盔无槽位元数据，靠游戏索引反查
            var isHelmetUnit = packages
                .Select(static package => NormalizeArchiveId(Path.GetFileNameWithoutExtension(package)))
                .Any(id => id is not null && _helmetNames.Value.ContainsKey(id));
            if (isHelmetUnit)
                unit.IsFromHelmetPackage = true;
            var armorId = packages
                .Select(static package => NormalizeArchiveId(Path.GetFileNameWithoutExtension(package)))
                .FirstOrDefault(id => id is not null &&
                    _armorNames.Value.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name));
            if (armorId is null)
            {
                unnamedUnits.Add(unit);
                continue;
            }
            if (!groups.TryGetValue(armorId, out var list))
            {
                list = [];
                groups.Add(armorId, list);
            }
            list.Add(unit);
        }

        // 只有一组有名护甲时，未识别部件（通常是配套头盔/装饰）并入该组；
        // 多组时保留为独立"未识别"组由用户在 UI 决定是否移植。
        if (groups.Count == 1 && unnamedUnits.Count > 0)
        {
            var groupArmorId = groups.Keys.First();
            groups[groupArmorId].AddRange(unnamedUnits);
            warnings.Add(_localizationService["ArmorSwapPage.WarningMergedUnidentified"]
                .Replace("{count}", unnamedUnits.Count.ToString())
                .Replace("{armor}", _armorNames.Value[groupArmorId]));
            unnamedUnits.Clear();
        }
        if (unassignedCount > 0)
            warnings.Add(_localizationService["ArmorSwapPage.WarningUnassignedUnits"]
                .Replace("{count}", unassignedCount.ToString()));

        var resultGroups = groups
            .Select(pair => new ArmorSwapSourceGroup
            {
                ArmorId = pair.Key,
                ArmorName = _armorNames.Value[pair.Key],
                Units = pair.Value.OrderBy(static unit => unit.FileId).ToArray()
            })
            .OrderBy(static group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unnamedUnits.Count > 0)
        {
            resultGroups.Add(new ArmorSwapSourceGroup
            {
                ArmorId = string.Empty,
                ArmorName = _localizationService["ArmorSwapPage.UnidentifiedGroup"]
                    .Replace("{count}", unnamedUnits.Count.ToString()),
                Units = unnamedUnits.OrderBy(static unit => unit.FileId).ToArray()
            });
        }

        // 目录视图：枚举所有含 patch 的目录（根目录 + 选项/子选项目录），
        // 产物模组按相同目录结构生成，保留来源的选项切换能力。
        var directoryViews = new List<ArmorSwapDirectoryView>();
        foreach (var (relativeDirectory, directoryPatches) in EnumeratePatchDirectories(mod.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = await BuildMergedViewsAsync(directoryPatches, cancellationToken);
            directoryViews.Add(new ArmorSwapDirectoryView
            {
                RelativeDirectory = relativeDirectory,
                Units = view.Units,
                MaterialEntries = view.MaterialEntries,
                TextureEntries = view.TextureEntries,
                TemplateHeader = view.TemplateHeader
            });
        }

        return new ArmorSwapSourceAnalysis
        {
            Mod = mod,
            Groups = resultGroups,
            Warnings = warnings,
            MaterialEntries = materialEntries,
            TextureEntries = textureEntries,
            DirectoryViews = directoryViews
        };
    }

    /// <summary>一个 patch 集合的合并视图（按部署顺序 FileId 后覆盖先）。</summary>
    private sealed record MergedView(
        IReadOnlyList<ArmorSwapUnitStructure> Units,
        int UnitEntryCount,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)> MaterialEntries,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)> TextureEntries,
        byte[] TemplateHeader);

    private async Task<MergedView> BuildMergedViewsAsync(
        IReadOnlyList<FileInfo> patchFiles,
        CancellationToken cancellationToken)
    {
        var unitEntries = new Dictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize)>();
        var materialEntries = new Dictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)>();
        var textureEntries = new Dictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)>();

        foreach (var patch in patchFiles)
        {
            var entries = await _inspectionService.ReadPatchEntriesAsync(patch, cancellationToken);
            foreach (var entry in entries)
            {
                if (entry.TypeId == UnitTypeId)
                    unitEntries[entry.FileId] = (patch.FullName, entry.MainOffset, entry.MainSize, entry.GpuOffset, entry.GpuSize);
                else if (entry.TypeId == MaterialTypeId)
                    materialEntries[entry.FileId] = (patch.FullName, entry.MainOffset, entry.MainSize);
                else if (entry.TypeId == TextureTypeId)
                    textureEntries[entry.FileId] = (patch.FullName, entry.MainOffset, entry.MainSize, entry.GpuOffset, entry.GpuSize, entry.StreamOffset, entry.StreamSize);
            }
        }

        var units = new List<ArmorSwapUnitStructure>(unitEntries.Count);
        foreach (var (fileId, position) in unitEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var main = await _inspectionService.ReadEntryMainAsync(new FileInfo(position.PatchPath), fileId, cancellationToken);
            if (main is null)
                continue;
            var structure = PatchResourceInspectionService.ParseArmorSwapUnitStructure(
                main, unchecked((long)fileId), position.PatchPath, position.GpuOffset, position.GpuSize);
            if (structure is not null)
                units.Add(structure);
        }

        // 模板 header 取自该组第一个 patch（游戏不校验其中的内容相关字段）
        byte[] templateHeader = new byte[72];
        foreach (var patch in patchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = await ReadEntryAtAsync(patch.FullName, 0, 72, cancellationToken);
            if (header is not null)
            {
                templateHeader = header;
                break;
            }
        }

        return new MergedView(units, unitEntries.Count, materialEntries, textureEntries, templateHeader);
    }

    /// <summary>
    /// 枚举模组内所有含主 patch 文件的目录（根目录 + 选项/子选项目录）。
    /// 目录相对路径使用 '/' 分隔，与 manifest Include 路径一致。
    /// </summary>
    private static IEnumerable<(string RelativeDirectory, List<FileInfo> Patches)> EnumeratePatchDirectories(
        DirectoryInfo modDirectory)
    {
        var candidates = new List<(DirectoryInfo Directory, string Relative)>
        {
            (modDirectory, string.Empty)
        };
        foreach (var directory in modDirectory.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            candidates.Add((directory, Path.GetRelativePath(
                modDirectory.FullName, directory.FullName).Replace('\\', '/')));
        }

        foreach (var (directory, relative) in candidates)
        {
            var patches = directory.GetFiles()
                .Where(static file => ModService.IsMainPatchFileName(file.Name))
                .OrderBy(static file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (patches.Count > 0)
                yield return (relative, patches);
        }
    }

    /// <summary>
    /// 从游戏 bundles 读取目标护甲 B 的骨架 Unit 视图：body 包 + 同名变体包 +
    /// 头盔包（按护甲名从 SDK 头盔表关联）。同名变体包（如 B-01 的 4 个
    /// Variation）游戏运行时随机选用，必须全部纳入骨架并逐一覆盖，否则游戏
    /// 随机到未覆盖变体时部件显示原版/污染内容。旧结构护甲（0x00A4CD36）的
    /// 头盔在独立的头盔包里，只读 body 包会导致头盔/未分类部件缺失。
    /// 返回 null 表示游戏目录不可用或 B 在游戏索引中不存在。
    /// </summary>
    public async Task<ArmorSwapTargetArmor?> LoadTargetArmorAsync(
        string armorId,
        CancellationToken cancellationToken = default)
    {
        var armorName = _armorNames.Value.TryGetValue(armorId, out var name) ? name : string.Empty;
        // body 包：选中包优先（共享 FileId 的配对归属以它为准），随后同名变体包
        var bodyPackageIds = ResolveVariationPackageIds(armorId)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        bodyPackageIds.Remove(armorId);
        bodyPackageIds.Insert(0, armorId);
        // 头盔包：SDK 头盔表里护甲名（规范化后）匹配的包
        var helmetPackageIds = ResolveHelmetPackageIds(armorName);

        var units = new List<ArmorSwapUnitStructure>();
        var packages = new List<ArmorSwapTargetPackage>();
        var seenFileIds = new HashSet<long>();
        foreach (var packageId in bodyPackageIds.Concat(helmetPackageIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var isHelmetPackage = helmetPackageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase);
            var packageUnits = await _versionCheckService.ResolveGameArmorUnitsAsync([packageId], cancellationToken);
            if (!packageUnits.TryGetValue(packageId, out var unitIds) || unitIds.Count == 0)
                continue;
            var parsedUnitIds = new List<long>(unitIds.Count);
            foreach (var unitId in unitIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 变体包之间共享大量 Unit（同一 FileId），结构只保留首次出现，
                // 但包的 Unit 归属列表完整保留（配对按包分配需要完整槽位组）
                if (seenFileIds.Add(unitId))
                {
                    var main = await _versionCheckService.ReadGameUnitMainDataAsync(unitId, cancellationToken);
                    if (main is null)
                        continue;
                    var structure = PatchResourceInspectionService.ParseArmorSwapUnitStructure(
                        main, unitId, isFromHelmetPackage: isHelmetPackage, packageId: packageId);
                    if (structure is null)
                        continue;
                    units.Add(structure);
                }
                parsedUnitIds.Add(unitId);
            }
            if (parsedUnitIds.Count > 0)
                packages.Add(new ArmorSwapTargetPackage(packageId, parsedUnitIds));
        }

        if (units.Count == 0)
            return null;

        return new ArmorSwapTargetArmor
        {
            ArmorId = armorId,
            ArmorName = armorName,
            Units = units,
            Packages = packages
        };
    }

    /// <summary>同名（规范化后）body 变体包集合（含 armorId 自身）。</summary>
    private IReadOnlyList<string> ResolveVariationPackageIds(string armorId)
    {
        if (!_armorNames.Value.TryGetValue(armorId, out var name) || string.IsNullOrWhiteSpace(name))
            return [armorId];
        var normalized = NormalizeArmorName(name);
        return _armorNames.Value
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value) &&
                string.Equals(NormalizeArmorName(pair.Value), normalized, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>SDK 头盔表里护甲名（规范化后）匹配的头盔包集合。</summary>
    private IReadOnlyList<string> ResolveHelmetPackageIds(string armorName)
    {
        if (string.IsNullOrWhiteSpace(armorName))
            return [];
        var normalized = NormalizeArmorName(armorName);
        return _helmetNames.Value
            .Where(pair => string.Equals(NormalizeArmorName(pair.Value), normalized, StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>护甲名规范化（去掉 " (Variation N)" / " (Set N)" 后缀）用于 body/头盔包关联。</summary>
    private static string NormalizeArmorName(string name)
    {
        var trimmed = name.Trim();
        var open = trimmed.LastIndexOf('(');
        if (open > 0)
            trimmed = trimmed[..open].TrimEnd();
        return trimmed;
    }

    /// <summary>
    /// 检查来源组与目标护甲的兼容性：错误阻断整体，警告跳过单项。
    /// </summary>
    public async Task<IReadOnlyList<ArmorSwapIssue>> CheckCompatibilityAsync(
        ArmorSwapSourceGroup source,
        ArmorSwapTargetArmor target,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<ArmorSwapIssue>();
        var pairedCount = 0;

        foreach (var sourceUnit in source.SlotUnits)
        {
            var match = target.SlotUnits.FirstOrDefault(candidate =>
                candidate.Slot == sourceUnit.Slot && candidate.BodyShape == sourceUnit.BodyShape);
            if (match is null)
            {
                issues.Add(new ArmorSwapIssue
                {
                    IsError = false,
                    Message = _localizationService["ArmorSwapPage.WarningMissingTargetSlot"]
                        .Replace("{slot}", sourceUnit.Slot.ToString())
                        .Replace("{shape}", sourceUnit.BodyShape.ToString())
                });
                continue;
            }

            pairedCount++;
            // 骨骼引用随来源网格走（产物保留 A 的 BonesId），不需要与目标一致；
            // 这里仅校验网格数据可读。
            if (sourceUnit.GpuSize == 0 || sourceUnit.SourcePatchPath is null)
            {
                issues.Add(new ArmorSwapIssue
                {
                    IsError = true,
                    Message = _localizationService["ArmorSwapPage.ErrorGpuUnavailable"]
                        .Replace("{slot}", sourceUnit.Slot.ToString())
                });
            }
        }

        if (source.UnclassifiedUnits.Count != target.UnclassifiedUnits.Count)
        {
            issues.Add(new ArmorSwapIssue
            {
                IsError = false,
                Message = _localizationService["ArmorSwapPage.WarningUnclassifiedCount"]
                    .Replace("{source}", source.UnclassifiedUnits.Count.ToString())
                    .Replace("{target}", target.UnclassifiedUnits.Count.ToString())
            });
        }
        else if (source.UnclassifiedUnits.Count > 0)
        {
            pairedCount += source.UnclassifiedUnits.Count;
        }

        if (pairedCount == 0)
        {
            issues.Add(new ArmorSwapIssue
            {
                IsError = true,
                Message = _localizationService["ArmorSwapPage.ErrorNoPairableUnits"]
            });
        }

        return issues;
    }

    /// <summary>
    /// 生成换甲产物模组（临时目录）：按来源目录结构（选项/子选项目录）逐目录生成
    /// patch（B 的骨架 Unit + A 的网格 + 材质/纹理），产物保留来源的选项切换能力。
    /// 返回临时目录路径。失败抛出异常，不残留部分产物。
    /// </summary>
    public async Task<string> GenerateArmorSwapModAsync(
        ModData sourceMod,
        ArmorSwapSourceAnalysis analysis,
        ArmorSwapSourceGroup source,
        ArmorSwapTargetArmor target,
        CancellationToken cancellationToken = default)
    {
        // 单护甲模组（一个护甲组，含"未识别"组）：所有选项目录的部件全部移植
        // （用户生成后可在产物里自由勾选任意选项，包括生成时未勾选的）；
        // 多护甲替换模组按所选护甲组过滤。
        var singleArmorMod = analysis.Groups.Count <= 1;
        var groupUnitIds = singleArmorMod
            ? null
            : source.Units.Select(static unit => unit.FileId).ToHashSet();
        var tempRoot = Path.Combine(_settingsService.TempDirectory, "ArmorSwap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 复制来源图标（产物 manifest 沿用）
            if (!string.IsNullOrWhiteSpace(sourceMod.Manifest.IconPath))
            {
                var iconSource = Path.Combine(sourceMod.Directory.FullName, sourceMod.Manifest.IconPath);
                if (File.Exists(iconSource))
                    File.Copy(iconSource, Path.Combine(tempRoot, Path.GetFileName(iconSource)), overwrite: true);
            }

            // 全局配对映射：来源 FileId → 目标 Unit。跨目录一致且不同的来源 FileId
            // 映射到不同的目标 FileId——护甲同一槽位常有多层叠加（内衬层/外套层），
            // 若各选项目录独立配对会把多层映射到同一目标 FileId，部署后互相覆盖，
            // 表现为"胸口露身体、上层衣服消失"。
            // 空气/占位网格（GPU 极小）不参与配对：老 SDK 模组的占位 Unit 会把
            // 目标的真实变体替换成空网格（表现为"原版护甲还在/部件缺失"）。
            // Helmet 例外：合体模组的头盔槽位空气是作者刻意为之（隐藏头盔）。
            const uint minPortableGpuBytes = 8000;
            var allSourceUnits = analysis.DirectoryViews
                .SelectMany(static view => view.Units)
                .Where(unit => groupUnitIds is null || groupUnitIds.Contains(unit.FileId))
                .Where(static unit =>
                    unit.GpuSize >= minPortableGpuBytes ||
                    unit.Slot == ModelPreviewCustomizationSlot.Helmet)
                .ToArray();
            var globalPairings = BuildGlobalPairings(allSourceUnits, target);
            if (globalPairings.Count == 0)
                throw new InvalidOperationException(_localizationService["ArmorSwapPage.ErrorNoPairableUnits"]);

            var generatedPatchCount = 0;
            foreach (var view in analysis.DirectoryViews)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var viewUnits = view.Units
                    .Where(unit => globalPairings.ContainsKey(unit.FileId))
                    .ToArray();
                if (viewUnits.Length == 0)
                    continue;

                var resources = await BuildDirectoryResourcesAsync(
                    viewUnits, globalPairings, view, analysis, target, cancellationToken);
                if (resources.Count == 0)
                    continue;

                var outputDirectory = view.RelativeDirectory.Length == 0
                    ? tempRoot
                    : Path.Combine(tempRoot, view.RelativeDirectory);
                Directory.CreateDirectory(outputDirectory);
                var patchPath = Path.Combine(outputDirectory, "9ba626afa44a3aa3.patch_0");
                new PatchWriter().WritePatchFiles(patchPath, view.TemplateHeader, resources);
                await ValidateOutputAsync(patchPath, target, cancellationToken);
                generatedPatchCount++;
            }

            if (generatedPatchCount == 0)
                throw new InvalidOperationException(_localizationService["ArmorSwapPage.ErrorNoPairableUnits"]);
            _logger.LogInformation("Armor swap output generated for {Source} -> {Target}: {Count} patch(es) in {Root}",
                sourceMod.Manifest.Name, target.DisplayName, generatedPatchCount, tempRoot);
            return tempRoot;
        }
        catch
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to clean up the failed armor swap output at {Path}", tempRoot);
            }
            throw;
        }
    }

    /// <summary>
    /// 建立全局配对映射（来源 FileId → 目标 Unit 列表）。同名护甲的多个变体包
    /// （如 B-01 的 4 个 Variation）游戏运行时随机选用，配对**逐包进行**：
    /// 每个变体包的每个 (Slot, BodyShape) 组独立从来源拿完整层分配（来源可跨包
    /// 复用，目标 FileId 全局只归属一次，共享 Unit 以先出现的包为准），保证
    /// 游戏随机到任何变体时显示都完整；否则未覆盖变体显示原版/污染内容。
    /// - 有槽位元数据的按 (Slot, BodyShape) 分组，组内来源 GPU 降序、目标 FileId 升序
    ///   分配，保证同槽位的多个来源层（内衬/外套等）映射到目标的不同变体；
    ///   目标组无同体型来源时回退到同槽位其他体型（取最大 GPU 的组）；
    ///   来源层多于目标变体时复用最后一个目标（选项覆盖语义：链珠/腰裙等
    ///   同槽位替换层各自生成到选项目录，部署时按勾选互相覆盖）；来源不足时
    ///   复用最后一个来源覆盖目标剩余变体（避免目标原版残留显示）；
    /// - 头盔包中无槽位元数据的 Unit（旧结构头盔）配对来源 Helmet 槽位 Unit；
    /// - 无槽位元数据的其余目标（旧结构护甲部件）配对来源无元数据 Unit。
    /// </summary>
    private static IReadOnlyDictionary<long, IReadOnlyList<ArmorSwapUnitStructure>> BuildGlobalPairings(
        IReadOnlyList<ArmorSwapUnitStructure> allSourceUnits,
        ArmorSwapTargetArmor target)
    {
        var assignedTargets = new HashSet<long>();
        // 目标 FileId → 已映射来源 FileId 集合（变体包逐包分配时排除已占用层）
        var targetSources = new Dictionary<long, HashSet<long>>();
        var result = new Dictionary<long, List<ArmorSwapUnitStructure>>();

        // 同一 FileId 出现在多个选项目录时（选项覆盖本体），优先选"信息最全"的
        // 版本（有 CustomizationInfo 优先，其次 GPU 窗口大的），否则选项变体
        // （如内裤链珠）会被本体占位版本顶掉而无法配对、选项目录缺失。
        var slotSources = allSourceUnits
            .Where(static unit => unit.Slot != ModelPreviewCustomizationSlot.Unknown)
            .GroupBy(static unit => unit.FileId)
            .Select(static group => group
                .OrderByDescending(static unit => unit.HasCustomizationInfo)
                .ThenByDescending(static unit => unit.GpuSize)
                .First())
            .ToArray();
        var sourceGroups = slotSources
            .GroupBy(static unit => (unit.Slot, unit.BodyShape))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static unit => unit.GpuSize)
                    .ThenBy(static unit => (ulong)unit.FileId)
                    .ToArray());
        var sourceHelmets = slotSources
            .Where(static unit => unit.Slot == ModelPreviewCustomizationSlot.Helmet)
            .OrderByDescending(static unit => unit.GpuSize)
            .ThenBy(static unit => (ulong)unit.FileId)
            .ToArray();
        var sourceUnclassified = allSourceUnits
            .Where(static unit => !unit.HasCustomizationInfo)
            .GroupBy(static unit => unit.FileId)
            .Select(static group => group.OrderByDescending(static unit => unit.GpuSize).First())
            .OrderBy(static unit => (ulong)unit.FileId)
            .ToArray();

        // 逐包分配（Packages 按加载顺序，用户选中的包在最前：共享 FileId 的
        // 配对归属以选中包为准）。每包的槽位组是完整的（含与其他变体包共享的
        // Unit），变体包独有 Unit 只拿"本包还没分到的层"（ExcludeUsedSources）。
        var structures = target.Units.ToDictionary(static unit => unit.FileId);
        foreach (var package in target.Packages)
        {
            var packageUnits = package.UnitIds
                .Select(id => structures.TryGetValue(id, out var unit) ? unit : null)
                .Where(static unit => unit is not null)
                .Select(static unit => unit!)
                .ToArray();

            // 1. 槽位组（含头盔包中带槽位元数据的 Helmet 单元）
            foreach (var slotGroup in packageUnits
                         .Where(static unit => unit.Slot != ModelPreviewCustomizationSlot.Unknown)
                         .GroupBy(static unit => (unit.Slot, unit.BodyShape))
                         .OrderBy(static group => group.Key.Slot)
                         .ThenBy(static group => group.Key.BodyShape))
            {
                var groupUnits = slotGroup.ToArray();
                var targets = groupUnits
                    .Where(unit => !assignedTargets.Contains(unit.FileId))
                    .OrderBy(static unit => (ulong)unit.FileId)
                    .ToArray();
                if (targets.Length == 0)
                    continue;
                var sources = ResolveSourceGroup(sourceGroups, slotGroup.Key.Slot, slotGroup.Key.BodyShape);
                if (sources.Length == 0)
                    continue;
                AssignGroup(result, assignedTargets, targetSources,
                    ExcludeUsedSources(sources, groupUnits, targetSources), targets);
            }

            // 2. 头盔包中无槽位元数据的 Unit（旧结构头盔，只能靠 SDK 头盔表识别）
            var helmetTargets = packageUnits
                .Where(static unit => unit.IsFromHelmetPackage &&
                    unit.Slot == ModelPreviewCustomizationSlot.Unknown)
                .Where(unit => !assignedTargets.Contains(unit.FileId))
                .OrderBy(static unit => (ulong)unit.FileId)
                .ToArray();
            if (helmetTargets.Length > 0 && sourceHelmets.Length > 0)
                AssignGroup(result, assignedTargets, targetSources, sourceHelmets, helmetTargets);

            // 3. 无槽位元数据的其余目标（旧结构护甲部件）
            var unclassifiedTargets = packageUnits
                .Where(static unit => !unit.HasCustomizationInfo && !unit.IsFromHelmetPackage)
                .Where(unit => !assignedTargets.Contains(unit.FileId))
                .OrderBy(static unit => (ulong)unit.FileId)
                .ToArray();
            if (unclassifiedTargets.Length > 0 && sourceUnclassified.Length > 0)
                AssignGroup(result, assignedTargets, targetSources, sourceUnclassified, unclassifiedTargets);
        }

        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<ArmorSwapUnitStructure>)pair.Value);
    }

    /// <summary>
    /// 取目标 (Slot, BodyShape) 组的来源候选：优先同体型精确匹配；目标体型在来源中
    /// 不存在时回退到同槽位最大 GPU 的其他体型组（来源网格骨骼随 A，跨体型安全）。
    /// </summary>
    private static ArmorSwapUnitStructure[] ResolveSourceGroup(
        IReadOnlyDictionary<(ModelPreviewCustomizationSlot Slot, ModelPreviewBodyShape Shape), ArmorSwapUnitStructure[]> sourceGroups,
        ModelPreviewCustomizationSlot slot,
        ModelPreviewBodyShape shape)
    {
        if (sourceGroups.TryGetValue((slot, shape), out var exact))
            return exact;
        return sourceGroups
            .Where(pair => pair.Key.Slot == slot)
            .OrderByDescending(static pair => pair.Value.Max(static unit => unit.GpuSize))
            .Select(static pair => pair.Value)
            .FirstOrDefault() ?? [];
    }

    /// <summary>
    /// 排除本包组内已分配目标已占用的来源层：变体包的独有 Unit 只拿"该包还没
    /// 分到的层"，保证游戏随机到任何变体都显示完整的一套层（不重复不缺失）。
    /// 全部被占用时兜底复用最小来源（通常是空气层：隐藏变体多出来的部件）。
    /// </summary>
    private static ArmorSwapUnitStructure[] ExcludeUsedSources(
        IReadOnlyList<ArmorSwapUnitStructure> sources,
        IReadOnlyList<ArmorSwapUnitStructure> groupUnits,
        IReadOnlyDictionary<long, HashSet<long>> targetSources)
    {
        var used = new HashSet<long>();
        foreach (var unit in groupUnits)
        {
            if (targetSources.TryGetValue(unit.FileId, out var sourceIds))
                used.UnionWith(sourceIds);
        }
        if (used.Count == 0)
            return sources as ArmorSwapUnitStructure[] ?? sources.ToArray();
        var available = sources.Where(unit => !used.Contains(unit.FileId)).ToArray();
        return available.Length > 0 ? available : [sources[^1]];
    }

    /// <summary>
    /// 组内分配：来源 GPU 降序 → 目标 FileId 升序；来源多于目标时多余来源钳到
    /// 最后一个目标（选项目录各自生成，部署按勾选覆盖）；目标多于来源时剩余
    /// 目标复用最后一个来源（变体全覆盖，杜绝原版/污染残留）。
    /// </summary>
    private static void AssignGroup(
        Dictionary<long, List<ArmorSwapUnitStructure>> result,
        HashSet<long> assignedTargets,
        Dictionary<long, HashSet<long>> targetSources,
        IReadOnlyList<ArmorSwapUnitStructure> sources,
        IReadOnlyList<ArmorSwapUnitStructure> targets)
    {
        var targetIndex = 0;
        foreach (var sourceUnit in sources)
        {
            if (targetIndex >= targets.Count)
                targetIndex = targets.Count - 1;
            AddPairing(result, sourceUnit.FileId, targets[targetIndex]);
            TrackTargetSource(targetSources, targets[targetIndex], sourceUnit);
            assignedTargets.Add(targets[targetIndex].FileId);
            if (targetIndex < targets.Count - 1)
                targetIndex++;
        }
        while (targetIndex < targets.Count)
        {
            AddPairing(result, sources[^1].FileId, targets[targetIndex]);
            TrackTargetSource(targetSources, targets[targetIndex], sources[^1]);
            assignedTargets.Add(targets[targetIndex].FileId);
            targetIndex++;
        }
    }

    private static void TrackTargetSource(
        Dictionary<long, HashSet<long>> targetSources,
        ArmorSwapUnitStructure target,
        ArmorSwapUnitStructure source)
    {
        if (!targetSources.TryGetValue(target.FileId, out var sourceIds))
        {
            sourceIds = [];
            targetSources.Add(target.FileId, sourceIds);
        }
        sourceIds.Add(source.FileId);
    }

    private static void AddPairing(
        Dictionary<long, List<ArmorSwapUnitStructure>> result,
        long sourceFileId,
        ArmorSwapUnitStructure target)
    {
        if (!result.TryGetValue(sourceFileId, out var list))
        {
            list = [];
            result.Add(sourceFileId, list);
        }
        list.Add(target);
    }

    /// <summary>为一个目录视图构造产物资源条目（Unit + 材质 + 纹理）。</summary>
    private async Task<IReadOnlyList<PatchWriter.ResourceEntry>> BuildDirectoryResourcesAsync(
        IReadOnlyList<ArmorSwapUnitStructure> viewUnits,
        IReadOnlyDictionary<long, IReadOnlyList<ArmorSwapUnitStructure>> globalPairings,
        ArmorSwapDirectoryView view,
        ArmorSwapSourceAnalysis analysis,
        ArmorSwapTargetArmor target,
        CancellationToken cancellationToken)
    {
        // 同一目录内多个来源 Unit 可能映射同一目标（目标变体不足时复用），
        // 产物 patch 的 FileId 必须唯一：按目标去重，保留 GPU 窗口最大的来源。
        var pairs = viewUnits
            .Where(unit => globalPairings.TryGetValue(unit.FileId, out _))
            .SelectMany(unit => globalPairings[unit.FileId]
                .Select(target => (Source: unit, Target: target)))
            .GroupBy(static pair => pair.Target.FileId)
            .Select(group => group.OrderByDescending(static pair => pair.Source.GpuSize).First())
            .ToArray();
        if (pairs.Length == 0)
            return [];

        // 1. Unit 条目：FileId = B 的，MainData = A 的 + B 的属性头部，GPU = A 的窗口
        var unitResources = new List<PatchWriter.ResourceEntry>(pairs.Length);
        foreach (var (sourceUnit, targetUnit) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mainData = BuildUnitMainData(sourceUnit, targetUnit);
            byte[]? gpuData = null;
            if (sourceUnit.GpuSize > 0 && sourceUnit.SourcePatchPath is not null)
            {
                gpuData = await _inspectionService.ReadEntryGpuAsync(
                    new FileInfo(sourceUnit.SourcePatchPath), (ulong)sourceUnit.FileId, cancellationToken);
                if (gpuData is null)
                    throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorGpuUnavailable"]
                        .Replace("{slot}", sourceUnit.Slot.ToString()));
            }
            unitResources.Add(new PatchWriter.ResourceEntry(
                targetUnit.FileId, unchecked((long)UnitTypeId), mainData, gpuData));
        }

        // 2. 材质：目录自身材质 + 配对 Unit 引用的材质（目录找不到时从全模组合并找）
        var materialIds = view.MaterialEntries.Keys
            .Concat(pairs.SelectMany(static pair => pair.Source.MaterialIds))
            .Distinct()
            .ToArray();
        var materialResources = new List<PatchWriter.ResourceEntry>();
        foreach (var materialId in materialIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindEntryPosition(materialId, view.MaterialEntries, analysis.MaterialEntries, out var position))
                continue;
            var main = await ReadEntryAtAsync(position.PatchPath, position.MainOffset, position.MainSize, cancellationToken);
            if (main is null)
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorMaterialUnavailable"]
                    .Replace("{id}", $"0x{materialId:X16}"));
            materialResources.Add(new PatchWriter.ResourceEntry(
                unchecked((long)materialId), unchecked((long)MaterialTypeId), main));
        }

        // 3. 纹理：材质引用的纹理（目录找不到时从全模组合并找）
        var referencedTextureIds = new HashSet<ulong>();
        var knownTextureIds = view.TextureEntries.Keys
            .Concat(analysis.TextureEntries.Keys)
            .ToHashSet();
        foreach (var materialResource in materialResources)
        {
            foreach (var textureId in ScanResourceIds(materialResource.MainData, knownTextureIds))
                referencedTextureIds.Add(textureId);
        }
        var textureResources = new List<PatchWriter.ResourceEntry>();
        foreach (var textureId in referencedTextureIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindTexturePosition(textureId, view.TextureEntries, analysis.TextureEntries, out var position))
                continue;
            var main = await ReadEntryAtAsync(position.PatchPath, position.MainOffset, position.MainSize, cancellationToken);
            if (main is null)
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorTextureUnavailable"]
                    .Replace("{id}", $"0x{textureId:X16}"));
            byte[]? gpu = null;
            byte[]? stream = null;
            if (position.GpuSize > 0)
            {
                // 纹理 GPU 载荷在 patch 的 .gpu_resources 伴生文件里（偏移相对该文件）
                gpu = await ReadEntryAtAsync(position.PatchPath + ".gpu_resources", position.GpuOffset, position.GpuSize, cancellationToken);
                if (gpu is null)
                    throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorTextureUnavailable"]
                        .Replace("{id}", $"0x{textureId:X16}"));
            }
            if (position.StreamSize > 0)
            {
                stream = await ReadEntryAtAsync(position.PatchPath + ".stream", position.StreamOffset, position.StreamSize, cancellationToken);
                if (stream is null)
                    throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorTextureUnavailable"]
                        .Replace("{id}", $"0x{textureId:X16}"));
            }
            textureResources.Add(new PatchWriter.ResourceEntry(
                unchecked((long)textureId), unchecked((long)TextureTypeId), main, gpu, stream));
        }

        return unitResources
            .Concat(materialResources)
            .Concat(textureResources)
            .ToArray();
    }

    private static bool TryFindEntryPosition(
        ulong fileId,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)> local,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize)> global,
        out (string PatchPath, ulong MainOffset, uint MainSize) position)
    {
        if (local.TryGetValue(fileId, out position))
            return true;
        return global.TryGetValue(fileId, out position);
    }

    private static bool TryFindTexturePosition(
        ulong fileId,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)> local,
        IReadOnlyDictionary<ulong, (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize)> global,
        out (string PatchPath, ulong MainOffset, uint MainSize, ulong GpuOffset, uint GpuSize, ulong StreamOffset, uint StreamSize) position)
    {
        if (local.TryGetValue(fileId, out position))
            return true;
        return global.TryGetValue(fileId, out position);
    }

    /// <summary>
    /// 查询目标护甲被哪些已启用模组覆盖（污染）。结果：armorId → 覆盖模组名列表。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetArmorPollutionAsync(
        IReadOnlyCollection<string> armorIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (armorIds.Count == 0)
            return result;

        var armorUnitIds = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var armorId in armorIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 污染检查覆盖同名变体包与头盔包（如夏菲替换 B-01 全 Variation）
            var packageIds = ResolveVariationPackageIds(armorId)
                .Concat(ResolveHelmetPackageIds(
                    _armorNames.Value.TryGetValue(armorId, out var name) ? name : string.Empty))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var packageUnits = await _versionCheckService.ResolveGameArmorUnitsAsync(packageIds, cancellationToken);
            var unitIds = new HashSet<long>();
            foreach (var packageId in packageIds)
            {
                if (packageUnits.TryGetValue(packageId, out var ids))
                    unitIds.UnionWith(ids);
            }
            if (unitIds.Count > 0)
                armorUnitIds[armorId] = unitIds;
        }
        if (armorUnitIds.Count == 0)
            return result;

        var enabledMods = _modService.Mods.Where(static mod => mod.Enabled).ToArray();
        var pollution = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in enabledMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileInfo> patchFiles;
            try
            {
                patchFiles = _modService.GetSelectedPatchFiles(mod);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to enumerate selected patch files for mod {ModName}", mod.Manifest.Name);
                continue;
            }

            var affectedArmors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var patch in patchFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<PatchTocInspectionItem> entries;
                try
                {
                    entries = await _inspectionService.ReadPatchEntriesAsync(patch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to read patch entries of {Patch}", patch.Name);
                    continue;
                }
                foreach (var (armorId, unitIds) in armorUnitIds)
                {
                    if (affectedArmors.Contains(armorId))
                        continue;
                    if (entries.Any(entry => entry.TypeId == UnitTypeId && unitIds.Contains(unchecked((long)entry.FileId))))
                        affectedArmors.Add(armorId);
                }
            }

            foreach (var armorId in affectedArmors)
            {
                if (!pollution.TryGetValue(armorId, out var list))
                {
                    list = [];
                    pollution.Add(armorId, list);
                }
                list.Add(mod.Manifest.Name);
            }
        }

        foreach (var key in pollution.Keys.ToArray())
            pollution[key] = pollution[key].Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return pollution.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task ValidateOutputAsync(
        string patchPath,
        ArmorSwapTargetArmor target,
        CancellationToken cancellationToken)
    {
        var patchFile = new FileInfo(patchPath);
        var entries = await _inspectionService.ReadPatchEntriesAsync(patchFile, cancellationToken);
        var unitEntries = entries.Where(static entry => entry.TypeId == UnitTypeId).ToArray();
        if (unitEntries.Length == 0)
            throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorValidationUnitCount"]
                .Replace("{expected}", ">0")
                .Replace("{actual}", "0"));

        foreach (var unitEntry in unitEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 产物 Unit 的 FileId 必须属于目标护甲骨架
            if (!target.SlotUnits.Any(unit => (ulong)unit.FileId == unitEntry.FileId) &&
                !target.UnclassifiedUnits.Any(unit => (ulong)unit.FileId == unitEntry.FileId))
            {
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorValidationUnit"]
                    .Replace("{id}", $"0x{unitEntry.FileId:X16}"));
            }

            var main = await _inspectionService.ReadEntryMainAsync(patchFile, unitEntry.FileId, cancellationToken);
            if (main is null || PatchResourceInspectionService.ParseArmorSwapUnitStructure(main, unchecked((long)unitEntry.FileId)) is null)
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorValidationUnit"]
                    .Replace("{id}", $"0x{unitEntry.FileId:X16}"));

            var gpu = await _inspectionService.ReadEntryGpuAsync(patchFile, unitEntry.FileId, cancellationToken);
            if (gpu is null)
                throw new InvalidDataException(_localizationService["ArmorSwapPage.ErrorValidationGpu"]
                    .Replace("{id}", $"0x{unitEntry.FileId:X16}"));
        }
    }

    /// <summary>
    /// 产物 Unit 主数据 = 来源 A 的完整主数据（网格/调色板/偏移表/LOD 组全部自洽），
    /// 仅把头部属性区覆盖为目标 B 的：BonesId（0x08）除外——骨骼引用必须随网格
    /// （A 的顶点权重/调色板基于 A 的骨骼），覆盖成 B 的骨骼会导致蒙皮错位。
    /// 覆盖范围：0x00-0x07 未知、0x10-0x2F（StateMachineId/版本/未知字段）。
    /// </summary>
    private static byte[] BuildUnitMainData(ArmorSwapUnitStructure source, ArmorSwapUnitStructure target)
    {
        var result = source.MainData.ToArray();
        var copyLength = Math.Min(TargetHeaderBytes, Math.Min(source.MainData.Length, target.MainData.Length));
        if (copyLength > 0x08)
        {
            Array.Copy(target.MainData, 0, result, 0, 0x08);
            Array.Copy(target.MainData, 0x10, result, 0x10, copyLength - 0x10);
        }
        else if (copyLength > 0)
        {
            Array.Copy(target.MainData, 0, result, 0, copyLength);
        }
        return result;
    }

    private static string? NormalizeArchiveId(string value) =>
        value.Length == 16 && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;

    private static IReadOnlyDictionary<string, string> LoadArmorNames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "armor-names.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // 文件缺失或损坏时返回空目录，调用方按无护甲目录处理
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 头盔包名称表（SDK archivehashes 的 Helmet 表）：头盔包 ID → 护甲名。
    /// 旧结构护甲（0x00A4CD36）的头盔是独立包，换甲目标骨架必须包含它。
    /// </summary>
    private static IReadOnlyDictionary<string, string> LoadHelmetNames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "armor-helmet-names.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<ulong> ScanResourceIds(byte[] data, IReadOnlySet<ulong> ids)
    {
        if (data.Length < sizeof(ulong) || ids.Count == 0)
            return [];
        var matches = new HashSet<ulong>();
        for (var offset = 0; offset <= data.Length - sizeof(ulong); offset++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
            if (ids.Contains(value))
                matches.Add(value);
        }
        return matches.ToArray();
    }

    private async Task<byte[]?> ReadEntryAtAsync(
        string patchPath,
        ulong offset,
        uint size,
        CancellationToken cancellationToken)
    {
        if (size == 0 || size > MaxEntryPayloadBytes ||
            !File.Exists(patchPath))
            return null;
        await using var stream = new FileStream(
            patchPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (offset > (ulong)stream.Length || size > (ulong)stream.Length - offset)
            return null;
        var data = new byte[size];
        stream.Seek((long)offset, SeekOrigin.Begin);
        var read = 0;
        while (read < data.Length)
        {
            var count = await stream.ReadAsync(data.AsMemory(read), cancellationToken);
            if (count == 0)
                return null;
            read += count;
        }
        return data;
    }
}
