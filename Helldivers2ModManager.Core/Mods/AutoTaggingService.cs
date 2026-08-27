using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Core.Mods;

public sealed record AutoTagRequest(
    string DirectoryPath,
    IReadOnlyList<Guid> ExistingTagIds,
    IReadOnlyCollection<ModType> DetectedTypes);

public sealed record AutoTagApplicationResult(
    IReadOnlyList<TagSetting> Tags,
    IReadOnlyDictionary<string, IReadOnlyList<Guid>> TagIdsByPath,
    int ChangedCount);

public sealed class AutoTaggingService
{
    private static readonly IReadOnlyDictionary<ModType, string[]> TagAliases = new Dictionary<ModType, string[]>
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

    public AutoTagApplicationResult Apply(
        IReadOnlyList<AutoTagRequest> requests,
        IReadOnlyList<TagSetting> tags,
        IReadOnlyList<AutoTagMappingSetting> manualMappings,
        Func<ModType, string> localizedName,
        bool createMissingTags)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(manualMappings);
        ArgumentNullException.ThrowIfNull(localizedName);

        var workingTags = tags.ToList();
        var definitions = ModTypeDetectionService.BuiltInTags;
        var builtInIds = definitions.Select(static definition => definition.Id).ToHashSet();
        var mappings = manualMappings
            .Where(mapping => workingTags.Any(tag => tag.Id == mapping.TagId))
            .GroupBy(mapping => (ModType)mapping.Type)
            .ToDictionary(static grouping => grouping.Key, static grouping => grouping.First().TagId);
        var results = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.OrdinalIgnoreCase);
        var changedCount = 0;

        foreach (var request in requests)
        {
            var detectedIds = ResolveIds(
                workingTags,
                request.DetectedTypes,
                definitions,
                mappings,
                localizedName,
                createMissingTags,
                out _);
            var merged = Merge(request.ExistingTagIds, detectedIds, builtInIds);
            if (merged is not null)
            {
                changedCount++;
            }

            results[request.DirectoryPath] = merged ?? request.ExistingTagIds;
        }

        return new(workingTags, results, changedCount);
    }

    private static List<Guid> ResolveIds(
        List<TagSetting> tags,
        IReadOnlyCollection<ModType> types,
        IReadOnlyList<BuiltInTagDefinition> definitions,
        IReadOnlyDictionary<ModType, Guid> mappings,
        Func<ModType, string> localizedName,
        bool createMissingTags,
        out bool created)
    {
        created = false;
        var result = new List<Guid>();
        foreach (var type in types)
        {
            var definition = definitions.FirstOrDefault(item => item.Type == type);
            if (definition is null)
            {
                continue;
            }

            if (mappings.TryGetValue(type, out var mappedId) && tags.Any(tag => tag.Id == mappedId))
            {
                result.Add(mappedId);
                continue;
            }

            var existing = tags.FirstOrDefault(tag => MatchesTag(tag, definition, localizedName(type)));
            if (existing is not null)
            {
                result.Add(existing.Id);
                continue;
            }

            if (!createMissingTags)
            {
                continue;
            }

            tags.Add(new TagSetting(definition.Id, localizedName(type), definition.Color));
            created = true;
            result.Add(tags[^1].Id);
        }

        return result;
    }

    private static bool MatchesTag(TagSetting tag, BuiltInTagDefinition definition, string localizedTypeName)
    {
        if (tag.Id == definition.Id)
        {
            return true;
        }

        var name = tag.Name.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        return name.Equals(localizedTypeName, StringComparison.OrdinalIgnoreCase) ||
               (TagAliases.TryGetValue(definition.Type, out var aliases) &&
                aliases.Any(alias => name.Equals(alias, StringComparison.OrdinalIgnoreCase)));
    }

    internal static List<Guid>? Merge(
        IReadOnlyList<Guid> existing,
        IReadOnlyCollection<Guid> detectedIds,
        IReadOnlySet<Guid> builtInIds)
    {
        if (detectedIds.Count == 0)
        {
            return null;
        }

        var detected = detectedIds.ToHashSet();
        var merged = existing
            .Where(id => !builtInIds.Contains(id) || detected.Contains(id))
            .Distinct()
            .ToList();
        foreach (var id in detected)
        {
            if (!merged.Contains(id))
            {
                merged.Add(id);
            }
        }

        var oldSet = existing.ToHashSet();
        return merged.Count == oldSet.Count && merged.All(oldSet.Contains) ? null : merged;
    }
}
