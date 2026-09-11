using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 找出同一个游戏补丁中的 Unit 同时归属哪些护甲/头盔。
/// 护甲/头盔名称回退数据来自社区名称表（google sheet），护甲部分最初整理自
/// HD2SDK-CommunityEdition 的 archivehashes 数据；
/// 补丁 Unit 解析参考 hd2-repatcher 和 HD2SDK-CommunityEdition 的结构资料。
/// 来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition、
/// https://github.com/RaidingForPants/hd2-repatcher。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed partial class ArmorReuseService
{
    private readonly ILogger<ArmorReuseService> _logger;
    private readonly ModService _modService;
    private readonly VersionCheckService _versionCheckService;
    private readonly LocalizationService _localizationService;
    // HD2SDK-CommunityEdition 的 archivehashes.json 护甲部分，同时作为护甲 package 白名单。
    // Unit 可以出现在任意 package 中（包括只含 JSON 的任务、配置 package），不能仅凭
    // package 名就将其归类为护甲；头盔表（helmet-names.json）提供头盔 archive 的
    // 名称与白名单扩展，使连带覆盖检测同样覆盖头盔。
    private readonly Lazy<IReadOnlyDictionary<string, string>> _sdkArmorNames;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _sdkHelmetNames;

    public ArmorReuseService(
        ILogger<ArmorReuseService> logger,
        ModService modService,
        VersionCheckService versionCheckService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _modService = modService;
        _versionCheckService = versionCheckService;
        _localizationService = localizationService;
        _sdkArmorNames = new Lazy<IReadOnlyDictionary<string, string>>(LoadSdkArmorNames);
        _sdkHelmetNames = new Lazy<IReadOnlyDictionary<string, string>>(LoadSdkHelmetNames);
    }

    public async Task<ArmorReuseAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ModData> mods,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<SourceUnit>();
        var scannedPatchCount = 0;
        var scannedModCount = 0;

        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FileInfo> patchFiles;
            try
            {
                patchFiles = _modService.GetSelectedPatchFiles(mod);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to enumerate selected patch files for armor reuse scan: {ModName}", mod.Manifest.Name);
                continue;
            }

            if (patchFiles.Count > 0)
                scannedModCount++;

            foreach (var patchFile in patchFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedPatchCount++;
                var units = await _versionCheckService.ExtractUnitVersionsFromPatchFileAsync(patchFile);
                sources.AddRange(units.Select(unit => new SourceUnit(
                    mod.Manifest.Guid,
                    mod.Manifest.Name,
                    patchFile.FullName,
                    unit.FileId)));
            }
        }

        var gameUnitPackages = await _versionCheckService.ResolveGameUnitPackageNamesAsync(
            sources.Select(static source => source.UnitId).Distinct().ToArray(),
            cancellationToken);

        var records = sources
            .GroupBy(static source => new { source.ModGuid, source.ModName, source.PatchPath })
            .Select(group => BuildRecord(group.Key.ModGuid, group.Key.ModName, group, gameUnitPackages))
            .Where(static record => record is not null)
            .Cast<ArmorReuseRecord>()
            .OrderBy(static record => record.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static record => record.SourceArmorName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ArmorReuseAnalysisResult
        {
            ScannedModCount = scannedModCount,
            ScannedPatchCount = scannedPatchCount,
            ScannedUnitCount = sources.Count,
            Records = records,
        };
    }

    private ArmorReuseRecord? BuildRecord(
        Guid modGuid,
        string modName,
        IEnumerable<SourceUnit> sources,
        IReadOnlyDictionary<long, IReadOnlyList<string>> gameUnitPackages)
    {
        var armors = sources
            .SelectMany(source => gameUnitPackages.TryGetValue(source.UnitId, out var packageNames)
                ? packageNames
                    .Select(ResolveKnownArmor)
                    .Where(static armor => armor is not null)
                    .Select(armor => new ResolvedArmor(armor!.Id, armor.Name, source.UnitId))
                : [])
            .GroupBy(static armor => armor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ArmorId = group.Key,
                ArmorName = group.First().Name,
                UnitCount = group.Select(static armor => armor.UnitId).Distinct().Count(),
            })
            .OrderByDescending(static armor => armor.UnitCount)
            .ThenBy(static armor => armor.ArmorName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (armors.Length < 2)
            return null;

        // 一个 base archive patch 中 Unit 数量最多的护甲/头盔通常是作者的主替换目标；
        // 其余护甲/头盔会因同一 patch 被整体覆盖而受到连带影响。
        var sourceArmor = armors[0];
        var reusedArmors = armors.Skip(1).ToArray();

        return new ArmorReuseRecord
        {
            ModGuid = modGuid,
            ModName = modName,
            SourceArmorId = sourceArmor.ArmorId,
            SourceArmorName = sourceArmor.ArmorName,
            SharedUnitCount = reusedArmors.Sum(static armor => armor.UnitCount),
            ReusedBy = reusedArmors
                .Select(static armor => new ArmorReuseTarget { ArmorId = armor.ArmorId, ArmorName = armor.ArmorName })
                .ToArray(),
        };
    }

    private ResolvedArmor? ResolveKnownArmor(string gamePackageName)
    {
        var packageName = Path.GetFileNameWithoutExtension(gamePackageName).Trim();
        if (!IsArchiveId(packageName))
            return null;

        // 仅接纳已确认的护甲/头盔 archive ID。非十六进制名称（例如
        // packages/content/mission_tutorial）是普通 package，可能仅包含 JSON。
        // 护甲表优先（两表仅基础 archive 一个键重叠），头盔表兜底。
        if (_sdkArmorNames.Value.TryGetValue(packageName, out var armorName))
            return new ResolvedArmor(packageName, armorName);
        return _sdkHelmetNames.Value.TryGetValue(packageName, out var helmetName)
            ? new ResolvedArmor(packageName, helmetName)
            : null;
    }

    private IReadOnlyDictionary<string, string> LoadSdkArmorNames()
    {
        // armor-names.json 为从 Community Edition 名称表整理的回退数据。
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "armor-names.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load the Community SDK armor name fallback from {Path}", path);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private IReadOnlyDictionary<string, string> LoadSdkHelmetNames()
    {
        // helmet-names.json 为同一社区名称表（google sheet）的头盔部分，
        // 与护甲表共同构成已知 archive 白名单。
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "helmet-names.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load the Community SDK helmet name fallback from {Path}", path);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsArchiveId(string value) => value.Length == 16 && value.All(Uri.IsHexDigit);

    private sealed record SourceUnit(Guid ModGuid, string ModName, string PatchPath, long UnitId);
    private sealed record ResolvedArmor(string Id, string Name, long UnitId = 0);
}
