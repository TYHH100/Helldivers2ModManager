using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        builder.Append("conflict-cache-v2|");

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
        var resources = new Dictionary<long, List<ModConflictParticipant>>();
        var scannedPatchCount = 0;
        var scannedUnitCount = 0;
        var scannedMods = 0;

        for (var deploymentOrder = 0; deploymentOrder < deploymentMods.Count; deploymentOrder++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mod = deploymentMods[deploymentOrder];
            if (!mod.Enabled)
                continue;

            scannedMods++;
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

            foreach (var patchFile in patchFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedPatchCount++;

                var units = await _versionCheckService.ExtractUnitVersionsFromPatchFileAsync(patchFile);
                foreach (var unit in units)
                {
                    scannedUnitCount++;
                    if (!resources.TryGetValue(unit.FileId, out var participants))
                    {
                        participants = [];
                        resources.Add(unit.FileId, participants);
                    }

                    participants.Add(new ModConflictParticipant
                    {
                        ModGuid = mod.Manifest.Guid,
                        ModName = mod.Manifest.Name,
                        PatchFileName = unit.FileName,
                        UnitId = unit.FileId,
                        Version = unit.Version,
                        DataSize = unit.DataSize,
                        DeploymentOrder = deploymentOrder,
                    });
                }
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
