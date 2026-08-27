using CoreModOption = Helldivers2ModManager.Core.Mods.ModOption;
using CoreModSubOption = Helldivers2ModManager.Core.Mods.ModSubOption;

namespace Helldivers2ModManager.Adapters;

internal static class CoreManifestMapper
{
    public static Core.Mods.IModManifest Map(Models.IModManifest manifest) => manifest switch
    {
        Models.LegacyModManifest legacy => new Core.Mods.LegacyModManifest
        {
            Guid = legacy.Guid,
            Name = legacy.Name,
            Description = legacy.Description,
            IconPath = legacy.IconPath,
            Options = legacy.Options,
        },
        Models.V1ModManifest v1 => new Core.Mods.V1ModManifest
        {
            Guid = v1.Guid,
            Name = v1.Name,
            Description = v1.Description,
            IconPath = v1.IconPath,
            Options = v1.Options?.Select(MapOption).ToArray(),
        },
        _ => throw new NotSupportedException("Unknown manifest version!"),
    };

    private static CoreModOption MapOption(Models.ModOption option) => new(
        option.Name,
        option.Description,
        option.Include,
        option.Image,
        option.SubOptions?.Select(MapSubOption).ToArray());

    private static CoreModSubOption MapSubOption(Models.ModSubOption option) => new(
        option.Name,
        option.Description,
        option.Include,
        option.Image);
}