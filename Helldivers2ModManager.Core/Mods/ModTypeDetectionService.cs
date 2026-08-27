using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.PatchKit;

namespace Helldivers2ModManager.Core.Mods;

public sealed record BuiltInTagDefinition(ModType Type, Guid Id, string NameKey, string Color);

public sealed record ModTypeDetectionResult(
    ModType Type,
    IReadOnlyList<ModType> Types,
    IReadOnlyDictionary<ulong, int> TypeIdCounts,
    IReadOnlyList<string> PathHints,
    int PatchesScanned,
    string? Reason);

public sealed partial class ModTypeDetectionService
{
    private const int SniffBytes = 64;
    private const int TextureHeaderOffset = 0xC0;
    private const int MaxPathScanBytes = 8 * 1024 * 1024;
    private const int ChunkPathScanBytes = 1024 * 1024;
    private const int MaxPathHints = 32;
    private const int MaxTypeTags = 4;

    internal const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;
    internal const ulong TextureTypeId = 0xCD4238C6A0C69E32UL;
    internal const ulong MaterialTypeId = 0xEAC0B497876ADEDFUL;
    internal const ulong BonesTypeId = 0x18DEAD01056B72E9UL;
    internal const ulong AnimationTypeId = 0x931E336D7646CC26UL;
    internal const ulong StateMachineTypeId = 0xA486D4045106165CUL;
    internal const ulong AudioTypeId = 0x535A7BD3E650D799UL;
    internal const ulong ScriptTypeId = 0xA14E8DFA2CD117E2UL;

    private static readonly ModType[] TypePriority =
    [
        ModType.Enemy,
        ModType.Audio,
        ModType.Script,
        ModType.PrimaryWeapon,
        ModType.SupportWeapon,
        ModType.Stratagem,
        ModType.Armor,
        ModType.Ui,
        ModType.Texture,
        ModType.Model,
    ];

    private static readonly IReadOnlyList<BuiltInTagDefinition> TagDefinitions =
    [
        new(ModType.Audio, new("D1C3A7B0-0000-4000-8000-000000000001"), "ModType.Tag.Audio", "#F97316"),
        new(ModType.Ui, new("D1C3A7B0-0000-4000-8000-000000000002"), "ModType.Tag.Ui", "#8B5CF6"),
        new(ModType.Texture, new("D1C3A7B0-0000-4000-8000-000000000003"), "ModType.Tag.Texture", "#06B6D4"),
        new(ModType.Armor, new("D1C3A7B0-0000-4000-8000-000000000004"), "ModType.Tag.Armor", "#10B981"),
        new(ModType.Stratagem, new("D1C3A7B0-0000-4000-8000-000000000005"), "ModType.Tag.Stratagem", "#3B82F6"),
        new(ModType.SupportWeapon, new("D1C3A7B0-0000-4000-8000-000000000006"), "ModType.Tag.SupportWeapon", "#EF4444"),
        new(ModType.Enemy, new("D1C3A7B0-0000-4000-8000-000000000007"), "ModType.Tag.Enemy", "#7F1D1D"),
        new(ModType.Model, new("D1C3A7B0-0000-4000-8000-000000000008"), "ModType.Tag.Model", "#EC4899"),
        new(ModType.PrimaryWeapon, new("D1C3A7B0-0000-4000-8000-000000000009"), "ModType.Tag.PrimaryWeapon", "#F59E0B"),
        new(ModType.Script, new("D1C3A7B0-0000-4000-8000-00000000000A"), "ModType.Tag.Script", "#64748B"),
    ];

    private readonly ConcurrentDictionary<string, PatchScanResult> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly PatchFileParser patchParser = new();

    public static IReadOnlyList<BuiltInTagDefinition> BuiltInTags => TagDefinitions;

    public async Task<IReadOnlyDictionary<string, ModTypeDetectionResult>> DetectAllAsync(
        IEnumerable<DirectoryInfo> modDirectories,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentDictionary<string, ModTypeDetectionResult>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            modDirectories,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ConcurrencyPolicy.GetIoParallelism(Environment.ProcessorCount),
                CancellationToken = cancellationToken,
            },
            async (directory, token) =>
            {
                var detection = await DetectAsync(directory, token).ConfigureAwait(false);
                results[directory.FullName] = detection;
            }).ConfigureAwait(false);
        return results;
    }

    public async Task<ModTypeDetectionResult> DetectAsync(
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<ulong, int>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasBankHeader = false;
        var hasTexture = false;
        var hasLua = false;
        var patchesScanned = 0;
        List<ModType>? patchTypes = null;

        if (modDirectory.Exists)
        {
            patchTypes = [];
            var files = modDirectory.EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => PatchFileRules.IsPatchFile(file.Name))
                .ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PatchFileRules.TryParse(file.Name, out var patchFile) || patchFile.Kind != PatchFileKind.Main)
                {
                    continue;
                }

                var scan = await ScanCachedAsync(file, cancellationToken).ConfigureAwait(false);
                if (scan is null)
                {
                    continue;
                }

                patchesScanned++;
                foreach (var (typeId, count) in scan.TypeIdCounts)
                {
                    counts[typeId] = counts.GetValueOrDefault(typeId) + count;
                }
                foreach (var path in scan.PathHints)
                {
                    paths.Add(path);
                }
                hasBankHeader |= scan.HasBankHeader;
                hasTexture |= scan.HasTexture;
                hasLua |= scan.HasLua;
                patchTypes.Add(ClassifyPatch(scan.TypeIdCounts, scan.HasBankHeader, scan.HasTexture, scan.HasLua, scan.PathHints, out _));
            }
        }

        var modLabel = ClassifyPatch(counts, hasBankHeader, hasTexture, hasLua, paths, out var reason);
        var types = AggregateTypes(modLabel, patchTypes ?? []);
        if (patchesScanned > 1)
        {
            reason += $" across {patchesScanned} patches";
        }

        return new(
            types.Count > 0 ? types[0] : ModType.Unknown,
            types,
            counts,
            paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
            patchesScanned,
            reason);
    }

    internal static ModType ClassifyPatch(
        IReadOnlyDictionary<ulong, int> counts,
        bool hasBankHeader,
        bool hasTexture,
        bool hasLua,
        IReadOnlyCollection<string> paths,
        out string? reason)
    {
        var unitCount = counts.GetValueOrDefault(UnitTypeId);
        var textureCount = counts.GetValueOrDefault(TextureTypeId);
        var materialCount = counts.GetValueOrDefault(MaterialTypeId);
        var bonesCount = counts.GetValueOrDefault(BonesTypeId);
        var animationCount = counts.GetValueOrDefault(AnimationTypeId);
        var stateMachineCount = counts.GetValueOrDefault(StateMachineTypeId);
        var audioCount = counts.GetValueOrDefault(AudioTypeId);
        var scriptCount = counts.GetValueOrDefault(ScriptTypeId);

        var enemyPath = paths.FirstOrDefault(IsEnemyPath);
        if (enemyPath is not null)
        {
            reason = "enemy path: " + enemyPath;
            return ModType.Enemy;
        }
        if (scriptCount > 0 || hasLua)
        {
            reason = $"script/lua (typeId={scriptCount}, lua={hasLua})";
            return ModType.Script;
        }
        if (audioCount > 0 || hasBankHeader)
        {
            reason = $"audio bank (typeId={audioCount}, BKHD={hasBankHeader})";
            return ModType.Audio;
        }

        var primaryWeaponPath = paths.FirstOrDefault(IsPrimaryWeaponPath);
        if (primaryWeaponPath is not null)
        {
            reason = "primary weapon path: " + primaryWeaponPath;
            return ModType.PrimaryWeapon;
        }
        var supportWeaponPath = paths.FirstOrDefault(IsSupportWeaponPath);
        if (supportWeaponPath is not null)
        {
            reason = "support weapon path: " + supportWeaponPath;
            return ModType.SupportWeapon;
        }
        var stratagemPath = paths.FirstOrDefault(IsStratagemPath);
        if (stratagemPath is not null)
        {
            reason = "stratagem path: " + stratagemPath;
            return ModType.Stratagem;
        }

        if (unitCount > 0)
        {
            if (bonesCount > 0 && stateMachineCount > 0)
            {
                if (animationCount > 0)
                {
                    reason = $"unit({unitCount})+bones+stateMachine+animation({animationCount})";
                    return ModType.SupportWeapon;
                }

                reason = $"unit({unitCount})+bones+stateMachine";
                return ModType.Stratagem;
            }
            if (materialCount > 0 && textureCount > 0 && unitCount >= 2)
            {
                reason = $"unit({unitCount})+texture({textureCount})+material({materialCount})";
                return ModType.Armor;
            }

            reason = $"unit({unitCount})";
            return ModType.Model;
        }
        if (textureCount > 0 && materialCount > 0)
        {
            reason = $"texture({textureCount})+material({materialCount})";
            return ModType.Texture;
        }
        if (textureCount > 0)
        {
            var uiPath = paths.FirstOrDefault(IsUiPath);
            reason = uiPath is not null ? "ui path: " + uiPath : $"texture({textureCount})";
            return ModType.Ui;
        }

        var audioPath = paths.FirstOrDefault(IsAudioPath);
        if (audioPath is not null)
        {
            reason = "audio path: " + audioPath;
            return ModType.Audio;
        }

        reason = counts.Count == 0 ? "no patch resources" : "unclear evidence";
        return ModType.Unknown;
    }

    internal static IReadOnlyList<ModType> AggregateTypes(ModType modLabel, IReadOnlyList<ModType> patchTypes)
    {
        var types = new HashSet<ModType>();
        foreach (var type in patchTypes.Where(static type => type != ModType.Unknown))
        {
            types.Add(type);
        }
        if (modLabel != ModType.Unknown)
        {
            types.Add(modLabel);
        }
        if (types.Count == 0)
        {
            return [ModType.Unknown];
        }

        if (types.Overlaps([ModType.Enemy, ModType.Audio, ModType.Script, ModType.PrimaryWeapon, ModType.SupportWeapon, ModType.Stratagem, ModType.Armor, ModType.Model]))
        {
            types.Remove(ModType.Ui);
        }
        if (types.Count > 1)
        {
            types.Remove(ModType.Model);
        }

        return types
            .OrderBy(type => Array.IndexOf(TypePriority, type))
            .Take(MaxTypeTags)
            .ToArray();
    }

    private async Task<PatchScanResult?> ScanCachedAsync(FileInfo file, CancellationToken cancellationToken)
    {
        file.Refresh();
        if (!file.Exists)
        {
            return null;
        }

        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var parsed = await patchParser.ParseFileAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (parsed.Snapshot is null)
        {
            return null;
        }

        var scan = await ScanPayloadsAsync(parsed.Snapshot, file, cancellationToken).ConfigureAwait(false);
        cache.TryAdd(key, scan);
        return scan;
    }

    private static async Task<PatchScanResult> ScanPayloadsAsync(
        PatchFileSnapshot snapshot,
        FileInfo file,
        CancellationToken cancellationToken)
    {
        var counts = snapshot.Entries.GroupBy(static entry => entry.TypeId)
            .ToDictionary(group => group.Key, group => group.Count());
        var hasBankHeader = false;
        var hasTexture = false;
        var hasLua = false;
        var sniffBuffer = new byte[SniffBytes];

        await using var stream = Open(file);
        foreach (var entry in snapshot.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.MainInRange(stream.Length))
            {
                continue;
            }

            var sniffLength = (int)Math.Min(SniffBytes, entry.MainSize);
            if (!await ReadAtAsync(stream, (long)entry.MainOffset, sniffBuffer.AsMemory(0, sniffLength), cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var payload = sniffBuffer.AsSpan(0, sniffLength);
            hasBankHeader |= ContainsAscii(payload, "BKHD") || ContainsAscii(payload, "DIDX");
            hasLua |= ContainsAscii(payload, "local ") || ContainsAscii(payload, "require(");
            if (entry.MainSize >= TextureHeaderOffset + 4 &&
                await ReadAtAsync(stream, (long)entry.MainOffset + TextureHeaderOffset, sniffBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false) &&
                sniffBuffer.AsSpan(0, 4).SequenceEqual("DDS "u8))
            {
                hasTexture = true;
            }
        }

        return new(counts, ScanPathStrings(stream), hasBankHeader, hasTexture, hasLua);
    }

    private static IReadOnlyList<string> ScanPathStrings(FileStream stream)
    {
        if (stream.Length <= MaxPathScanBytes)
        {
            var data = new byte[(int)stream.Length];
            if (!ReadAt(stream, 0, data))
            {
                return [];
            }

            return ExtractPaths(data);
        }

        var head = new byte[ChunkPathScanBytes];
        var tail = new byte[ChunkPathScanBytes];
        if (!ReadAt(stream, 0, head) || !ReadAt(stream, stream.Length - ChunkPathScanBytes, tail))
        {
            return [];
        }

        return ExtractPaths(head)
            .Concat(ExtractPaths(tail))
            .Take(MaxPathHints)
            .ToArray();
    }

    private static List<string> ExtractPaths(byte[] data)
    {
        var result = new List<string>();
        if (data.Length == 0)
        {
            return result;
        }

        foreach (Match match in AsciiStringRegex().Matches(Encoding.ASCII.GetString(data)))
        {
            if (result.Count >= MaxPathHints)
            {
                break;
            }
            if (match.Value.Contains('/'))
            {
                result.Add(match.Value);
            }
        }

        return result;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string needle)
    {
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        for (var index = 0; index <= data.Length - needleBytes.Length; index++)
        {
            if (data.Slice(index, needleBytes.Length).SequenceEqual(needleBytes))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEnemyPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        return normalized.Contains("/enemies/", StringComparison.Ordinal) ||
               normalized.Contains("characters/enemies", StringComparison.Ordinal) ||
               normalized.Contains("vo_bugs", StringComparison.Ordinal) ||
               normalized.Contains("vo_terminid", StringComparison.Ordinal) ||
               normalized.Contains("vo_automaton", StringComparison.Ordinal) ||
               normalized.Contains("vo_illuminate", StringComparison.Ordinal);
    }

    private static bool IsUiPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        return normalized.Contains("content/ui", StringComparison.Ordinal) ||
               normalized.Contains("/ui/", StringComparison.Ordinal) ||
               normalized.Contains("stratagem_icons", StringComparison.Ordinal) ||
               normalized.Contains("strategy_icons", StringComparison.Ordinal) ||
               normalized.Contains("strategem_icons", StringComparison.Ordinal);
    }

    private static bool IsPrimaryWeaponPath(string path) =>
        path.Contains("equipment/primary_weapons", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/primary_weapons/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportWeaponPath(string path) =>
        path.Contains("equipment/support_weapons", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/support_weapons/", StringComparison.OrdinalIgnoreCase);

    private static bool IsStratagemPath(string path)
    {
        var normalized = path.ToLowerInvariant();
        return normalized.Contains("equipment/backpacks", StringComparison.Ordinal) ||
               normalized.Contains("/backpacks/", StringComparison.Ordinal) ||
               normalized.Contains("/strategems/", StringComparison.Ordinal);
    }

    private static bool IsAudioPath(string path) =>
        path.Contains("content/audio", StringComparison.OrdinalIgnoreCase);

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            return false;
        }

        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count <= 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static async ValueTask<bool> ReadAtAsync(
        Stream stream,
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
        {
            return false;
        }

        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count <= 0)
            {
                return false;
            }

            read += count;
        }

        return true;
    }

    private static FileStream Open(FileInfo file) => new(
        file.FullName,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        81920,
        FileOptions.SequentialScan);

    [GeneratedRegex("[ -~]{12,}")]
    private static partial Regex AsciiStringRegex();
}

internal sealed record PatchScanResult(
    IReadOnlyDictionary<ulong, int> TypeIdCounts,
    IReadOnlyList<string> PathHints,
    bool HasBankHeader,
    bool HasTexture,
    bool HasLua);
