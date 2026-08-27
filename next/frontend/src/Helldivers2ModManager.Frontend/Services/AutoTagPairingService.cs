using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class AutoTagPairingService(ApplicationSettingsService settings, LocalizationCatalog localization)
{
    public IReadOnlyList<BuiltInTagDefinition> Definitions => ModTypeDetectionService.BuiltInTags;

    public IReadOnlyList<TagSetting> Tags => [.. settings.Current.Tags];

    public Guid? GetMapping(ModType type) => settings.Current.AutoTagMappings
        .FirstOrDefault(mapping => (ModType)mapping.Type == type)?.TagId;

    public Guid? GetExistingTagForType(ModType type)
    {
        var definition = Definitions.First(item => item.Type == type);
        var name = localization.GetString(definition.NameKey);
        return settings.Current.Tags.FirstOrDefault(tag => tag.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public async Task SaveAsync(IReadOnlyList<AutoTagMappingSetting> mappings, CancellationToken cancellationToken = default)
    {
        settings.Current.AutoTagMappings = [.. mappings];
        await settings.SaveAsync(settings.Current, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TagSetting> CreateTypeTagAsync(ModType type, CancellationToken cancellationToken = default)
    {
        var definition = Definitions.First(item => item.Type == type);
        var name = localization.GetString(definition.NameKey);
        if (settings.Current.Tags.Any(tag => tag.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Tag \"{name}\" already exists.");
        }

        var tag = new TagSetting(Guid.NewGuid(), name, definition.Color);
        settings.Current.Tags = new List<TagSetting>(settings.Current.Tags) { tag };
        await settings.SaveAsync(settings.Current, cancellationToken).ConfigureAwait(false);
        return tag;
    }
}

public sealed record AutoTagPairingItemModel(
    ModType Type,
    string TypeName,
    string Color,
    Guid? SelectedTagId,
    IReadOnlyList<TagSetting> Tags);
