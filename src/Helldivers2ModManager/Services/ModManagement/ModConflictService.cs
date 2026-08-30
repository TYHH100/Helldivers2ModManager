using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Helldivers2ModManager.Services;

/// <summary>
/// 分析当前部署配置中的跨模组资源覆盖关系。
/// 这里报告的是资源组合覆盖，不会修改任何模组文件。
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModConflictService
{
    private readonly ILogger<ModConflictService> _logger;
    private readonly ModService _modService;
    private readonly VersionCheckService _versionCheckService;

    /// <summary>
    /// 首次冲突扫描（缓存未命中）按模组并行的最大并发数；
    /// 补丁解析以磁盘 IO 为主，2-4 个并发即可显著缩短全量扫描耗时。
    /// </summary>
    private static readonly int MaxConflictScanParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);

    public ModConflictService(
        ILogger<ModConflictService> logger,
        ModService modService,
        VersionCheckService versionCheckService)
    {
        _logger = logger;
        _modService = modService;
        _versionCheckService = versionCheckService;
    }

    /// <summary>
    /// 基于当前实际部署配置生成稳定签名。
    /// 仅依赖已启用模组、部署顺序与选项状态，不扫描补丁文件。
    /// </summary>
    public string BuildCacheKey(IReadOnlyList<ModData> deploymentMods)
    {
        var builder = new StringBuilder(deploymentMods.Count * 96 + 32);
        builder.Append("conflict-cache-v3|");

        for (var index = 0; index < deploymentMods.Count; index++)
        {
            var mod = deploymentMods[index];
            builder.Append(index).Append('|');
            builder.Append(mod.Manifest.Guid.ToString("N")).Append('|');
            builder.Append(mod.Manifest.Version).Append('|');
            builder.Append(mod.Directory.LastWriteTimeUtc.Ticks).Append('|');
            AppendBoolArray(builder, mod.EnabledOptions);
            builder.Append('|');
            AppendIntArray(builder, mod.SelectedOptions);
            builder.Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// 按传入顺序扫描模组；列表顺序应与实际部署顺序一致。
    /// </summary>
    public async Task<ModConflictAnalysisResult> AnalyzeAsync(
        IReadOnlyList<ModData> deploymentMods,
        CancellationToken cancellationToken = default)
    {
        // 先收集启用模组（部署顺序），再按模组有界并行解析补丁：
        // 串行实现下缓存未命中时整个扫描被单模组 IO 拖慢，并行后结果仍按
        // 部署顺序合并，participant 顺序与串行实现完全一致（确定性输出）。
        var enabledMods = new List<(int DeploymentOrder, ModData Mod)>();
        for (var deploymentOrder = 0; deploymentOrder < deploymentMods.Count; deploymentOrder++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = deploymentMods[deploymentOrder];
            if (mod.Enabled)
                enabledMods.Add((deploymentOrder, mod));
        }

        var scannedMods = enabledMods.Count;
        var scannedPatchCount = 0;
        var scannedUnitCount = 0;
        var perModParticipants = new ConcurrentDictionary<int, List<ModConflictParticipant>>();

        await Parallel.ForEachAsync(
            enabledMods,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConflictScanParallelism,
                CancellationToken = cancellationToken
            },
            async (entry, token) =>
            {
                var (deploymentOrder, mod) = entry;
                IReadOnlyList<FileInfo> patchFiles;
                try
                {
                    patchFiles = _modService.GetSelectedPatchFiles(mod);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to enumerate selected patch files for mod {ModName}", mod.Manifest.Name);
                    return;
                }

                var participants = new List<ModConflictParticipant>();
                foreach (var patchFile in patchFiles)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref scannedPatchCount);

                    var units = await _versionCheckService.ExtractUnitVersionsFromPatchFileAsync(patchFile);
                    foreach (var unit in units)
                    {
                        Interlocked.Increment(ref scannedUnitCount);
                        participants.Add(new ModConflictParticipant
                        {
                            ModGuid = mod.Manifest.Guid,
                            ModName = mod.Manifest.Name,
                            PatchFileName = unit.FileName,
                            UnitId = unit.FileId,
                            Version = unit.Version,
                            DataSize = unit.DataSize,
                            GpuSize = unit.GpuSize,
                            DeploymentOrder = deploymentOrder,
                        });
                    }
                }
                perModParticipants[deploymentOrder] = participants;
            });

        // 按部署顺序合并（保持原串行实现的 participant 顺序）
        var resources = new Dictionary<long, List<ModConflictParticipant>>();
        foreach (var entry in enabledMods)
        {
            if (!perModParticipants.TryGetValue(entry.DeploymentOrder, out var participants))
                continue;
            foreach (var participant in participants)
            {
                if (!resources.TryGetValue(participant.UnitId, out var list))
                {
                    list = [];
                    resources.Add(participant.UnitId, list);
                }
                list.Add(participant);
            }
        }

        var displayNames = await _versionCheckService.ResolveGameUnitDisplayNamesAsync(resources.Keys.ToArray(), cancellationToken);
        var conflicts = resources
            .Select(static pair => new
            {
                UnitId = pair.Key,
                Participants = pair.Value
                    .GroupBy(static p => p.ModGuid)
                    .SelectMany(static group => group)
                    .ToArray()
            })
            .Where(static item => item.Participants.Select(static p => p.ModGuid).Distinct().Count() > 1)
            .OrderBy(static item => item.UnitId)
            .Select(item => new ModConflictRecord
            {
                UnitId = item.UnitId,
                FriendlyName = displayNames.TryGetValue(item.UnitId, out var name) ? name : string.Empty,
                OriginalName = $"0x{item.UnitId:X16}",
                Participants = item.Participants
            })
            .ToArray();

        return new ModConflictAnalysisResult
        {
            ScannedModCount = scannedMods,
            ScannedPatchCount = scannedPatchCount,
            ScannedUnitCount = scannedUnitCount,
            Conflicts = conflicts,
        };
    }

    private static void AppendBoolArray(StringBuilder builder, bool[] values)
    {
        builder.Append('[');
        foreach (var value in values)
            builder.Append(value ? '1' : '0').Append(',');
        builder.Append(']');
    }

    private static void AppendIntArray(StringBuilder builder, int[] values)
    {
        builder.Append('[');
        foreach (var value in values)
            builder.Append(value).Append(',');
        builder.Append(']');
    }

}
