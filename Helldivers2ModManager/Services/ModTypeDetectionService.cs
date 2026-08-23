using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.Services;

/// <summary>
/// Lightweight mod-type detection based on patch TOC TypeId distribution,
/// payload signatures (BKHD/DDS/Lua) and embedded plaintext path strings
/// (e.g. "content/audio/obj_generic_terminal").
/// Mixed mods are handled by classifying every patch independently and then
/// aggregating the distinct type tags at mod level.
/// Detection reads only the patch header, TOC entries and small bounded
/// payload/path scans; companion .gpu_resources/.stream files are never
/// loaded. CPU/IO heavy work must be invoked from a background thread.
/// </summary>
[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModTypeDetectionService
{
    private const int PatchHeaderMagic = unchecked((int)0xF0000011);
    private const int HeaderSize = 72;
    private const int TypeEntrySize = 32;
    private const int FileEntrySize = 80;
    private const int MaxTypes = 1000;
    private const int MaxFiles = 100000;
    private const int MaxPathScanBytes = 8 * 1024 * 1024;
    private const int ChunkPathScanBytes = 1 * 1024 * 1024;
    private const int MaxPathHints = 32;
    private const int MaxTypeTags = 4;
    private const int SniffBytes = 64;
    private const int TextureHeaderOffset = 0xC0;

    internal const ulong UnitTypeId = 0xE0A48D0BE9A7453FUL;
    internal const ulong TextureTypeId = 0xCD4238C6A0C69E32UL;
    internal const ulong MaterialTypeId = 0xEAC0B497876ADEDFUL;
    internal const ulong BonesTypeId = 0x18DEAD01056B72E9UL;
    internal const ulong AnimationTypeId = 0x931E336D7646CC26UL;
    internal const ulong StateMachineTypeId = 0xA486D4045106165CUL;
    internal const ulong AudioTypeId = 0x535A7BD3E650D799UL;
    internal const ulong PathEntryTypeId = 0xAF32095C82F2B070UL;
    internal const ulong ScriptTypeId = 0xA14E8DFA2CD117E2UL;

    private static readonly Regex s_asciiStringRegex = new("[ -~]{12,}", RegexOptions.Compiled);

    /// <summary>Tag priority: earlier entries win when ordering a mod's type set.</summary>
    private static readonly ModType[] s_typePriority =
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

    private readonly ILogger<ModTypeDetectionService> _logger;
    private readonly ConcurrentDictionary<string, PatchScanResult> _patchCache = new(StringComparer.OrdinalIgnoreCase);

    public ModTypeDetectionService(ILogger<ModTypeDetectionService> logger)
    {
        _logger = logger;
    }

    internal sealed record PatchScanResult(
        IReadOnlyDictionary<ulong, int> TypeIdCounts,
        IReadOnlyList<string> PathHints,
        bool HasBkHd,
        bool HasDds,
        bool HasLua);

    internal sealed record ModTypeDetectionResult(
        ModType Type,
        IReadOnlyList<ModType> Types,
        IReadOnlyDictionary<ulong, int> TypeIdCounts,
        IReadOnlyList<string> PathHints,
        int PatchesScanned,
        string? Reason);

    internal sealed record BuiltInTagDefinition(ModType Type, Guid Id, string NameKey, string Color);

    internal static readonly IReadOnlyList<BuiltInTagDefinition> BuiltInTagDefinitions =
    [
        new(ModType.Audio, new Guid("D1C3A7B0-0000-4000-8000-000000000001"), "ModType.Tag.Audio", "#F97316"),
        new(ModType.Ui, new Guid("D1C3A7B0-0000-4000-8000-000000000002"), "ModType.Tag.Ui", "#8B5CF6"),
        new(ModType.Texture, new Guid("D1C3A7B0-0000-4000-8000-000000000003"), "ModType.Tag.Texture", "#06B6D4"),
        new(ModType.Armor, new Guid("D1C3A7B0-0000-4000-8000-000000000004"), "ModType.Tag.Armor", "#10B981"),
        new(ModType.Stratagem, new Guid("D1C3A7B0-0000-4000-8000-000000000005"), "ModType.Tag.Stratagem", "#3B82F6"),
        new(ModType.SupportWeapon, new Guid("D1C3A7B0-0000-4000-8000-000000000006"), "ModType.Tag.SupportWeapon", "#EF4444"),
        new(ModType.Enemy, new Guid("D1C3A7B0-0000-4000-8000-000000000007"), "ModType.Tag.Enemy", "#7F1D1D"),
        new(ModType.Model, new Guid("D1C3A7B0-0000-4000-8000-000000000008"), "ModType.Tag.Model", "#EC4899"),
        new(ModType.PrimaryWeapon, new Guid("D1C3A7B0-0000-4000-8000-000000000009"), "ModType.Tag.PrimaryWeapon", "#F59E0B"),
        new(ModType.Script, new Guid("D1C3A7B0-0000-4000-8000-00000000000A"), "ModType.Tag.Script", "#64748B"),
    ];

    /// <summary>
    /// Detect mod types for all mods. Sync CPU/IO; call from a background thread.
    /// </summary>
    public Dictionary<string, ModTypeDetectionResult> DetectAll(
        IReadOnlyCollection<ModData> mods,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModTypeDetectionResult>(mods.Count, StringComparer.OrdinalIgnoreCase);
        var sync = new object();
        var concurrency = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        Parallel.ForEach(mods, new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = cancellationToken
        }, mod =>
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            var detection = Detect(mod.Directory);
            lock (sync)
            {
                result[mod.Directory.FullName] = detection;
            }
        });
        return result;
    }

    public ModTypeDetectionResult Detect(DirectoryInfo modDirectory)
    {
        var counts = new Dictionary<ulong, int>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasBkHd = false;
        var hasDds = false;
        var hasLua = false;
        var patches = 0;
        var patchTypes = new List<ModType>();

        if (modDirectory.Exists)
        {
            foreach (var file in modDirectory.EnumerateFiles("*.patch_*", SearchOption.AllDirectories))
            {
                if (file.Name.EndsWith(".gpu_resources", StringComparison.OrdinalIgnoreCase) ||
                    file.Name.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
                    continue;

                var scan = TryScanPatchCached(file);
                if (scan is null)
                    continue;
                patches++;

                foreach (var (typeId, count) in scan.TypeIdCounts)
                    counts[typeId] = counts.GetValueOrDefault(typeId) + count;
                foreach (var path in scan.PathHints)
                    paths.Add(path);
                hasBkHd |= scan.HasBkHd;
                hasDds |= scan.HasDds;
                hasLua |= scan.HasLua;

                patchTypes.Add(ClassifyPatch(
                    scan.TypeIdCounts,
                    scan.HasBkHd,
                    scan.HasDds,
                    scan.HasLua,
                    scan.PathHints,
                    out _));
            }
        }

        var modLabel = ClassifyPatch(counts, hasBkHd, hasDds, hasLua, paths, out var modReason);
        var types = AggregateTypes(modLabel, patchTypes);
        var reason = patches > 1
            ? modReason + " across " + patches + " patches"
            : modReason;

        return new ModTypeDetectionResult(
            types.Count > 0 ? types[0] : ModType.Unknown,
            types,
            counts,
            paths.OrderBy(static p => p, StringComparer.Ordinal).ToArray(),
            patches,
            reason);
    }

    /// <summary>
    /// Merge the detected type tag ids into each mod's TagIds while keeping user
    /// tags. Stale built-in tags that are no longer detected are removed so the
    /// auto tags always reflect the current detection. Returns the number of
    /// changed mods. Must be called on the UI thread.
    /// </summary>
    public int ApplyAutoTags(
        SettingsService settings,
        LocalizationService localization,
        IReadOnlyCollection<ModData> mods,
        IReadOnlyDictionary<string, ModTypeDetectionResult> detections,
        bool createMissingTags)
    {
        if (settings.IsReadonly || mods.Count == 0)
            return 0;

        var byType = new Dictionary<ModType, BuiltInTagDefinition>(BuiltInTagDefinitions.Count);
        var builtInIds = new HashSet<Guid>(BuiltInTagDefinitions.Count);
        foreach (var def in BuiltInTagDefinitions)
        {
            byType[def.Type] = def;
            builtInIds.Add(def.Id);
        }

        // 手动配对优先：仅保留仍存在对应标签的配对
        var manualMappings = new Dictionary<ModType, Guid>();
        foreach (var mapping in settings.AutoTagMappings)
        {
            if (settings.Tags.Any(t => t.Id == mapping.TagId))
                manualMappings[mapping.Type] = mapping.TagId;
        }

        var anyCreated = false;
        var changed = 0;
        foreach (var mod in mods)
        {
            if (!detections.TryGetValue(mod.Directory.FullName, out var detection))
                continue;

            var detectedIds = ResolveAutoTagIds(
                settings.Tags,
                detection.Types,
                byType,
                manualMappings,
                type => localization[byType[type].NameKey],
                createMissingTags,
                out var created);
            anyCreated |= created;

            var merged = MergeAutoTags(mod.TagIds, detectedIds, builtInIds);
            if (merged is null)
                continue;
            mod.TagIds = merged;
            changed++;
        }

        if (anyCreated)
            _ = settings.SaveAsync();
        return changed;
    }

    /// <summary>
    /// Resolve the actual tag ids for the detected types. Priority:
    /// 1) manual pairing (AutoTagMappings), 2) existing tag matched by stable
    /// built-in id or by name (localized name or known aliases), so tags users
    /// created manually in older versions are reused instead of being
    /// duplicated, 3) create a new tag when createMissingTags is true.
    /// </summary>
    internal static IReadOnlyList<Guid> ResolveAutoTagIds(
        IList<ModTag> tags,
        IReadOnlyCollection<ModType> types,
        IReadOnlyDictionary<ModType, BuiltInTagDefinition> defs,
        IReadOnlyDictionary<ModType, Guid> manualMappings,
        Func<ModType, string> localizedName,
        bool createMissingTags,
        out bool created)
    {
        created = false;
        var result = new List<Guid>();
        foreach (var type in types)
        {
            if (!defs.TryGetValue(type, out var def))
                continue;

            // 1. manual pairing wins when the mapped tag still exists
            if (manualMappings.TryGetValue(type, out var mappedId) &&
                tags.Any(t => t.Id == mappedId))
            {
                result.Add(mappedId);
                continue;
            }

            // 2. reuse an existing tag by id/name
            var existing = tags.FirstOrDefault(t => MatchesTag(t, def, localizedName(type)));
            if (existing is not null)
            {
                result.Add(existing.Id);
                continue;
            }

            // 3. optionally create
            if (!createMissingTags)
                continue;

            var tag = new ModTag(def.Id, localizedName(type), def.Color);
            tags.Add(tag);
            created = true;
            result.Add(tag.Id);
        }
        return result;
    }

    private static bool MatchesTag(ModTag tag, BuiltInTagDefinition def, string localizedName)
    {
        if (tag.Id == def.Id)
            return true;
        var name = tag.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return false;
        if (name.Equals(localizedName, StringComparison.OrdinalIgnoreCase))
            return true;
        return s_tagAliases.TryGetValue(def.Type, out var aliases) &&
               aliases.Any(alias => name.Equals(alias, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly IReadOnlyDictionary<ModType, string[]> s_tagAliases = new Dictionary<ModType, string[]>
    {
        [ModType.Audio] = ["音效", "Audio", "声音", "音效模组"],
        [ModType.Ui] = ["UI", "界面", "图标", "HUD"],
        [ModType.Texture] = ["贴图", "Texture", "纹理", "材质包"],
        [ModType.Armor] = ["护甲", "Armor", "装甲", "服装"],
        [ModType.Stratagem] = ["战略配备", "Stratagem", "战备"],
        [ModType.SupportWeapon] = ["支援武器", "Support Weapon", "SupportWeapon"],
        [ModType.Enemy] = ["敌人", "Enemy"],
        [ModType.Model] = ["模型", "Model"],
        [ModType.PrimaryWeapon] = ["主武器", "Primary Weapon", "PrimaryWeapon"],
        [ModType.Script] = ["脚本", "Script", "Lua"],
    };

    /// <summary>
    /// Pure merge logic: keeps user tags, removes stale built-in type tags,
    /// appends every detected type tag id. Returns null when nothing would change.
    /// </summary>
    internal static List<Guid>? MergeAutoTags(
        IReadOnlyList<Guid> existing,
        IReadOnlyCollection<Guid> detectedIds,
        IReadOnlySet<Guid> builtInIds)
    {
        if (detectedIds.Count == 0)
            return null;

        var detected = detectedIds.ToHashSet();
        var merged = existing.Where(id => !builtInIds.Contains(id) || detected.Contains(id)).Distinct().ToList();
        foreach (var id in detected)
        {
            if (!merged.Contains(id))
                merged.Add(id);
        }

        var oldSet = existing.ToHashSet();
        if (merged.Count == oldSet.Count && !merged.Any(id => !oldSet.Contains(id)))
            return null;
        return merged;
    }

    // ===== classification =====

    /// <summary>
    /// Classify a single patch by its own resource evidence. Path hints take
    /// priority because they carry precise game taxonomy (primary_weapons,
    /// support_weapons, backpacks, vo_bugs, content/ui ...).
    /// </summary>
    internal static ModType ClassifyPatch(
        IReadOnlyDictionary<ulong, int> counts,
        bool hasBkHd,
        bool hasDds,
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

        // 1. enemy path overrides everything
        var enemyPath = paths.FirstOrDefault(IsEnemyPath);
        if (enemyPath is not null)
        {
            reason = "enemy path: " + enemyPath;
            return ModType.Enemy;
        }

        // 2. script (Lua) resources
        if (scriptCount > 0 || hasLua)
        {
            reason = "script/lua (typeId=" + scriptCount + ", lua=" + hasLua + ")";
            return ModType.Script;
        }

        // 3. audio
        if (audioCount > 0 || hasBkHd)
        {
            reason = "audio bank (typeId=" + audioCount + ", BKHD=" + hasBkHd + ")";
            return ModType.Audio;
        }

        // 4. precise path taxonomy
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

        // 5. model family
        if (unitCount > 0)
        {
            if (bonesCount > 0 && stateMachineCount > 0)
            {
                if (animationCount > 0)
                {
                    reason = "unit(" + unitCount + ")+bones+stateMachine+animation(" + animationCount + ")";
                    return ModType.SupportWeapon;
                }
                reason = "unit(" + unitCount + ")+bones+stateMachine";
                return ModType.Stratagem;
            }

            if (materialCount > 0 && textureCount > 0 && unitCount >= 2)
            {
                reason = "unit(" + unitCount + ")+texture(" + textureCount + ")+material(" + materialCount + ")";
                return ModType.Armor;
            }

            reason = "unit(" + unitCount + ")";
            return ModType.Model;
        }

        // 6. texture family
        if (textureCount > 0 && materialCount > 0)
        {
            reason = "texture(" + textureCount + ")+material(" + materialCount + ")";
            return ModType.Texture;
        }

        if (textureCount > 0)
        {
            var uiPath = paths.FirstOrDefault(IsUiPath);
            reason = uiPath is not null ? "ui path: " + uiPath : "texture(" + textureCount + ")";
            return ModType.Ui;
        }

        // 7. audio path only (no audio type id / BKHD evidence)
        var audioPath = paths.FirstOrDefault(IsAudioPath);
        if (audioPath is not null)
        {
            reason = "audio path: " + audioPath;
            return ModType.Audio;
        }

        reason = counts.Count == 0 ? "no patch resources" : "unclear evidence";
        return ModType.Unknown;
    }

    /// <summary>
    /// Aggregate per-patch labels plus the mod-level structural label into the
    /// final ordered tag set. Model is a fallback (dropped when a more specific
    /// label exists); Ui is only kept when the whole mod carries no model-ish
    /// resources (so a texture option inside a weapon mod is not tagged UI).
    /// </summary>
    internal static IReadOnlyList<ModType> AggregateTypes(ModType modLabel, IReadOnlyList<ModType> patchTypes)
    {
        var set = new HashSet<ModType>();
        foreach (var type in patchTypes)
        {
            if (type != ModType.Unknown)
                set.Add(type);
        }
        if (modLabel != ModType.Unknown)
            set.Add(modLabel);
        if (set.Count == 0)
            return [ModType.Unknown];

        var hasModelish = set.Any(type => type is ModType.Enemy or ModType.Audio or ModType.Script or
            ModType.PrimaryWeapon or ModType.SupportWeapon or ModType.Stratagem or ModType.Armor or ModType.Model);
        if (hasModelish)
            set.Remove(ModType.Ui);
        if (set.Count > 1)
            set.Remove(ModType.Model);

        return set
            .OrderBy(type => Array.IndexOf(s_typePriority, type))
            .Take(MaxTypeTags)
            .ToArray();
    }

    private static bool IsEnemyPath(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.Contains("/enemies/", StringComparison.Ordinal) || p.Contains("characters/enemies", StringComparison.Ordinal))
            return true;
        if (p.Contains("vo_bugs", StringComparison.Ordinal) || p.Contains("vo_terminid", StringComparison.Ordinal) ||
            p.Contains("vo_automaton", StringComparison.Ordinal) || p.Contains("vo_illuminate", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static bool IsUiPath(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("content/ui", StringComparison.Ordinal) || p.Contains("/ui/", StringComparison.Ordinal) ||
               p.Contains("stratagem_icons", StringComparison.Ordinal) || p.Contains("strategy_icons", StringComparison.Ordinal) ||
               p.Contains("strategem_icons", StringComparison.Ordinal);
    }

    private static bool IsPrimaryWeaponPath(string path) =>
        path.Contains("equipment/primary_weapons", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/primary_weapons/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportWeaponPath(string path) =>
        path.Contains("equipment/support_weapons", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/support_weapons/", StringComparison.OrdinalIgnoreCase);

    private static bool IsStratagemPath(string path)
    {
        var p = path.ToLowerInvariant();
        return p.Contains("equipment/backpacks", StringComparison.Ordinal) ||
               p.Contains("/backpacks/", StringComparison.Ordinal) ||
               p.Contains("/strategems/", StringComparison.Ordinal);
    }

    private static bool IsAudioPath(string path) =>
        path.Contains("content/audio", StringComparison.OrdinalIgnoreCase);

    // ===== patch scanning =====

    private PatchScanResult? TryScanPatchCached(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists || file.Length < HeaderSize)
            return null;

        var key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (!_patchCache.TryGetValue(key, out var scan))
        {
            scan = TryScanPatch(file);
            if (scan is null)
                return null;
            _patchCache.TryAdd(key, scan);
        }
        return scan;
    }

    private PatchScanResult? TryScanPatch(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            var header = new byte[HeaderSize];
            if (!ReadAt(stream, 0, header) ||
                BinaryPrimitives.ReadInt32LittleEndian(header) != PatchHeaderMagic)
                return null;

            var numTypes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            var numFiles = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (numTypes < 0 || numFiles < 0 || numTypes > MaxTypes || numFiles > MaxFiles)
                return null;

            var fileEntriesOffset = HeaderSize + (long)numTypes * TypeEntrySize;
            if (fileEntriesOffset + (long)numFiles * FileEntrySize > stream.Length)
                return null;

            var counts = new Dictionary<ulong, int>();
            var hasBkHd = false;
            var hasDds = false;
            var hasLua = false;
            var entryBuffer = new byte[FileEntrySize];
            var sniffBuffer = new byte[SniffBytes];
            var ddsMagic = new byte[4];

            for (var i = 0; i < numFiles; i++)
            {
                if (!ReadAt(stream, fileEntriesOffset + i * FileEntrySize, entryBuffer))
                    return null;

                var typeId = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(8));
                counts[typeId] = counts.GetValueOrDefault(typeId) + 1;

                var mainOffset = BinaryPrimitives.ReadUInt64LittleEndian(entryBuffer.AsSpan(16));
                var mainSize = BinaryPrimitives.ReadUInt32LittleEndian(entryBuffer.AsSpan(56));
                if (mainSize >= 16 && mainOffset <= (ulong)stream.Length && mainOffset + 16 <= (ulong)stream.Length)
                {
                    var sniffLength = (int)Math.Min(SniffBytes, mainSize);
                    if (!ReadAt(stream, (long)mainOffset, sniffBuffer.AsSpan(0, sniffLength)))
                        continue;
                    if (ContainsAscii(sniffBuffer.AsSpan(0, sniffLength), "BKHD") ||
                        ContainsAscii(sniffBuffer.AsSpan(0, sniffLength), "DIDX"))
                        hasBkHd = true;
                    if (ContainsAscii(sniffBuffer.AsSpan(0, sniffLength), "local ") ||
                        ContainsAscii(sniffBuffer.AsSpan(0, sniffLength), "local BC") ||
                        ContainsAscii(sniffBuffer.AsSpan(0, sniffLength), "require("))
                        hasLua = true;
                }

                if (mainSize >= TextureHeaderOffset + 4 &&
                    mainOffset <= (ulong)stream.Length &&
                    mainOffset + TextureHeaderOffset + 4 <= (ulong)stream.Length &&
                    ReadAt(stream, (long)mainOffset + TextureHeaderOffset, ddsMagic) &&
                    ddsMagic.AsSpan().SequenceEqual("DDS "u8))
                    hasDds = true;
            }

            var paths = ScanPathStrings(stream);
            return new PatchScanResult(counts, paths, hasBkHd, hasDds, hasLua);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Patch scan failed: {File}", file.FullName);
            return null;
        }
    }

    private static IReadOnlyList<string> ScanPathStrings(FileStream stream)
    {
        if (stream.Length <= MaxPathScanBytes)
        {
            var data = new byte[(int)stream.Length];
            if (!ReadAt(stream, 0, data))
                return [];
            return ExtractPaths(data);
        }

        var head = new byte[ChunkPathScanBytes];
        var tail = new byte[ChunkPathScanBytes];
        if (!ReadAt(stream, 0, head) || !ReadAt(stream, stream.Length - ChunkPathScanBytes, tail))
            return [];
        return ExtractPaths(head)
            .Concat(ExtractPaths(tail))
            .Take(MaxPathHints)
            .ToArray();
    }

    private static List<string> ExtractPaths(byte[] data)
    {
        var result = new List<string>();
        if (data.Length == 0)
            return result;

        var text = Encoding.ASCII.GetString(data);
        foreach (Match match in s_asciiStringRegex.Matches(text))
        {
            if (result.Count >= MaxPathHints)
                break;
            var value = match.Value;
            if (value.IndexOf('/') < 0)
                continue;
            result.Add(value);
        }
        return result;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string needle)
    {
        if (data.Length < needle.Length)
            return false;
        var needleBytes = Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i <= data.Length - needleBytes.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needleBytes.Length; j++)
            {
                if (data[i + j] != needleBytes[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    private static bool ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > stream.Length - buffer.Length)
            return false;
        stream.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer.Slice(read));
            if (count <= 0)
                return false;
            read += count;
        }
        return true;
    }
}
