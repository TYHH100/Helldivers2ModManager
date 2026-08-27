using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Versioning;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Analysis;

public sealed class ModConflictService(
    PatchStructureAnalyzer analyzer,
    GameArchiveService? gameArchiveService = null,
    ILogger<ModConflictService>? logger = null)
{
    private readonly GameArchiveService? _gameArchiveService = gameArchiveService;
    private readonly ILogger<ModConflictService>? _logger = logger;

    public static string BuildCacheKey(IReadOnlyList<AnalysisMod> mods)
    {
        var builder = new StringBuilder("conflict-cache-v3|");
        for (var index = 0; index < mods.Count; index++)
        {
            var mod = mods[index];
            builder.Append(mod.DeploymentOrder).Append('|').Append(mod.Id.ToString("N")).Append('|')
                .Append(mod.Version).Append('|')
                .Append(mod.Directory.LastWriteTimeUtc.Ticks).Append('|');
            foreach (var enabled in mod.EnabledOptions ?? []) builder.Append(enabled ? '1' : '0');
            builder.Append('|');
            foreach (var selected in mod.SelectedOptions ?? []) builder.Append(selected).Append(',');
            builder.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public Task<ConflictAnalysisResult> AnalyzeAsync(
        IReadOnlyList<AnalysisMod> mods,
        CancellationToken cancellationToken = default) =>
        AnalyzeCoreAsync(mods, FindDataDirectory(), cancellationToken);

    public Task<ConflictAnalysisResult> AnalyzeAsync(
        IReadOnlyList<AnalysisMod> mods,
        DirectoryInfo? gameDataDirectory,
        CancellationToken cancellationToken = default) =>
        AnalyzeCoreAsync(mods, gameDataDirectory, cancellationToken);

    private async Task<ConflictAnalysisResult> AnalyzeCoreAsync(
        IReadOnlyList<AnalysisMod> mods,
        DirectoryInfo? gameDataDirectory,
        CancellationToken cancellationToken)
    {
        var enabled = mods.Where(mod => mod.Enabled).OrderBy(mod => mod.DeploymentOrder).ToArray();
        var participants = new List<ConflictParticipant>();
        var displayNames = new Dictionary<long, string>();
        var patchCount = 0;
        var perMod = new ConcurrentDictionary<int, List<ConflictParticipant>>();
        await Parallel.ForEachAsync(
            enabled.Select(mod => (Mod: mod, Index: Array.IndexOf(enabled, mod))),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4), CancellationToken = cancellationToken },
            async (entry, token) =>
            {
                var modParticipants = new List<ConflictParticipant>();
                FileInfo[] files;
                try
                {
                    files = SelectPatchFiles(entry.Mod);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger?.LogWarning(exception, "Unable to enumerate selected patch files for conflict scan: {ModName}", entry.Mod.Name);
                    perMod[entry.Mod.DeploymentOrder] = [];
                    return;
                }

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref patchCount);
                    var analysis = await analyzer.AnalyzeFileAsync(file, token);
                    modParticipants.AddRange(analysis.UnitDetails.Select(unit => new ConflictParticipant(
                        entry.Mod.Id, entry.Mod.Name, Path.GetFileName(unit.FileName), unit.FileId, unit.Version,
                        unit.DataSize, unit.GpuSize, entry.Mod.DeploymentOrder)));
                }
                perMod[entry.Mod.DeploymentOrder] = modParticipants;
            });

        foreach (var mod in enabled)
        {
            if (!perMod.TryGetValue(mod.DeploymentOrder, out var modParticipants)) continue;
            participants.AddRange(modParticipants);
        }

        if (_gameArchiveService is { } gameArchive && gameDataDirectory is { Exists: true } && participants.Count > 0)
        {
            {
                var ids = participants.Select(participant => participant.UnitId).Distinct().ToArray();
                var lookup = await gameArchive.ResolveUnitsAsync(gameDataDirectory, ids, cancellationToken);
                foreach (var reference in lookup.References)
                    displayNames[reference.Key] = NormalizePackageName(reference.Value.PackageName);
            }
        }

        var conflicts = participants
            .GroupBy(participant => participant.UnitId)
            .Where(group => group.Select(participant => participant.ModId).Distinct().Count() > 1)
            .OrderBy(group => group.Key)
            .Select(group => new ConflictRecord(group.Key,
                displayNames.GetValueOrDefault(group.Key, string.Empty),
                group.ToArray(),
                $"0x{group.Key:X16}"))
            .ToArray();
        return new(enabled.Length, patchCount, participants.Count, conflicts);
    }

    private static FileInfo[] SelectPatchFiles(AnalysisMod mod)
    {
        if (mod.Manifest is not { } manifest || mod.EnabledOptions is null || mod.SelectedOptions is null)
            return [.. mod.Directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name))];
        return [.. ModPatchSelection.GetSelectedPatchFiles(mod.Directory, manifest, mod.EnabledOptions, mod.SelectedOptions)];
    }

    internal static string NormalizePackageName(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName)) return string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(packageName);
        return string.IsNullOrWhiteSpace(fileName) ? packageName.Trim() : fileName;
    }

    private static DirectoryInfo? FindDataDirectory()
    {
        var path = Environment.GetEnvironmentVariable("HELLDIVERS2_DATA_PATH") ?? Path.Combine("C:", "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2", "data");
        return new DirectoryInfo(path);
    }
}

public sealed class ArmorReuseService
{
    private readonly GameArchiveService? _gameArchive;
    private readonly ILogger<ArmorReuseService>? _logger;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _armorNames;
    private readonly PatchStructureAnalyzer _analyzer = new();

    public ArmorReuseService(GameArchiveService gameArchiveService, ILogger<ArmorReuseService>? logger = null) : this(gameArchiveService, logger, Path.Combine(AppContext.BaseDirectory, "Resources", "Data", "armor-names.json"))
    {
    }

    public ArmorReuseService(GameArchiveService gameArchiveService, ILogger<ArmorReuseService>? logger, string armorNamesPath)
    {
        _gameArchive = gameArchiveService;
        _logger = logger;
        _armorNames = new(() => LoadArmorNames(armorNamesPath, logger));
    }

    public async Task<ArmorReuseAnalysisResult> AnalyzeAsync(
        IReadOnlyList<AnalysisMod> mods,
        DirectoryInfo? gameDataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var sources = new List<(Guid Id, string Name, string Patch, long UnitId)>();
        var patchCount = 0;
        var scannedMods = 0;
        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo[] files;
            try
            {
                files = SelectPatchFiles(mod);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogWarning(exception, "Unable to enumerate selected patch files for armor reuse scan: {ModName}", mod.Name);
                continue;
            }

            if (files.Length > 0) scannedMods++;
            foreach (var file in files)
            {
                patchCount++;
                var analysis = await _analyzer.AnalyzeFileAsync(file, cancellationToken);
                sources.AddRange(analysis.UnitDetails.Select(unit => (mod.Id, mod.Name, file.FullName, unit.FileId)));
            }
        }
        if (sources.Count == 0) return new(scannedMods, patchCount, 0, []);

        if (gameDataDirectory is null)
        {
            return new(scannedMods, patchCount, sources.Count, []);
        }

        var lookup = await _gameArchive!.ResolveUnitsAsync(gameDataDirectory, sources.Select(source => source.UnitId).Distinct().ToArray(), cancellationToken);
        var records = sources.GroupBy(source => (source.Id, source.Name, source.Patch))
            .Select(group => BuildRecord(group.Key.Id, group.Key.Name, group, lookup.PackageNames))
            .Where(record => record is not null).Cast<ArmorReuseRecord>()
            .OrderBy(record => record.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(record => record.SourceArmorName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(scannedMods, patchCount, sources.Count, records);
    }

    internal ArmorReuseRecord? BuildRecord(
        Guid modId,
        string modName,
        IEnumerable<(Guid Id, string Name, string Patch, long UnitId)> sources,
        IReadOnlyDictionary<long, IReadOnlyList<string>> gameUnitPackages)
    {
        var armors = sources.SelectMany(source => gameUnitPackages.TryGetValue(source.UnitId, out var names)
            ? names.Select(name => ResolveKnownArmor(name, source.UnitId)).Where(armor => armor is not null).Select(armor => armor!.Value)
            : []).GroupBy(armor => armor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => (group.Key, group.First().Name, Count: group.Select(armor => armor.UnitId).Distinct().Count()))
            .OrderByDescending(armor => armor.Count).ThenBy(armor => armor.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (armors.Length < 2) return null;
        return new(modId, modName, armors[0].Key, armors[0].Name,
            armors.Skip(1).Select(armor => new ArmorReuseTarget(armor.Key, armor.Name)).ToArray(),
            armors.Skip(1).Sum(armor => armor.Count));
    }

    private static FileInfo[] SelectPatchFiles(AnalysisMod mod)
    {
        if (mod.Manifest is not { } manifest || mod.EnabledOptions is null || mod.SelectedOptions is null)
            return [.. mod.Directory.EnumerateFiles("*", SearchOption.AllDirectories).Where(file => PatchFileRules.IsMainPatchFile(file.Name))];
        return [.. ModPatchSelection.GetSelectedPatchFiles(mod.Directory, manifest, mod.EnabledOptions, mod.SelectedOptions)];
    }

    private (string Id, string Name, long UnitId)? ResolveKnownArmor(string packageName, long unitId)
    {
        var name = Path.GetFileNameWithoutExtension(packageName).Trim();
        return name.Length == 16 && name.All(Uri.IsHexDigit) && _armorNames.Value.TryGetValue(name, out var display)
            ? (name, display, unitId) : null;
    }

    private static DirectoryInfo FindDataDirectory()
    {
        var path = Environment.GetEnvironmentVariable("HELLDIVERS2_DATA_PATH") ?? Path.Combine("C:", "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2", "data");
        return new DirectoryInfo(path);
    }

    private PatchStructureAnalyzer analyzer() => fieldAnalyzer ??= new();

    private PatchStructureAnalyzer? fieldAnalyzer;

    private static IReadOnlyDictionary<string, string> LoadArmorNames(string path, ILogger<ArmorReuseService>? logger)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Unable to load armor names from {Path}", path);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}





