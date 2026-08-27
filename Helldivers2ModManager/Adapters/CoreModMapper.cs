using System.IO;
using Helldivers2ModManager.Models;
using CoreManifest = Helldivers2ModManager.Core.Mods;

namespace Helldivers2ModManager.Adapters;

internal static class CoreModMapper
{
    public static Models.IModManifest Map(CoreManifest.IModManifest manifest) => manifest switch
    {
        CoreManifest.LegacyModManifest legacy => new Models.LegacyModManifest
        {
            Guid = legacy.Guid,
            Name = legacy.Name,
            Description = legacy.Description,
            IconPath = legacy.IconPath,
            Options = legacy.Options?.ToArray(),
        },
        CoreManifest.V1ModManifest v1 => new Models.V1ModManifest
        {
            Guid = v1.Guid,
            Name = v1.Name,
            Description = v1.Description,
            IconPath = v1.IconPath,
            Options = v1.Options?.Select(MapOption).ToArray(),
            NexusData = v1.NexusData is null ? null : new()
            {
                ModId = v1.NexusData.ModId,
                Version = v1.NexusData.Version,
            },
        },
        _ => throw new NotSupportedException("Unknown manifest version!"),
    };

    public static ModProblem MapProblem(CoreManifest.ArchiveImportProblem problem)
    {
        var sourceDirectory = new DirectoryInfo(Path.GetDirectoryName(problem.ArchivePath) ?? problem.ArchivePath);
        return new ModProblem
        {
            Directory = sourceDirectory,
            Kind = problem.Kind switch
            {
                CoreManifest.ArchiveImportProblemKind.CannotReadArchive => ModProblemKind.CantReadArchive,
                CoreManifest.ArchiveImportProblemKind.NoManifestFound => ModProblemKind.NoManifestFound,
                CoreManifest.ArchiveImportProblemKind.Duplicate => ModProblemKind.Duplicate,
                CoreManifest.ArchiveImportProblemKind.EmptyOptions => ModProblemKind.EmptyOptions,
                CoreManifest.ArchiveImportProblemKind.EmptySubOptions => ModProblemKind.EmptySubOptions,
                CoreManifest.ArchiveImportProblemKind.EmptyIncludes => ModProblemKind.EmptyIncludes,
                CoreManifest.ArchiveImportProblemKind.InvalidPath => ModProblemKind.InvalidPath,
                _ => ModProblemKind.CantReadArchive,
            },
            ExtraData = problem.Detail,
        };
    }

    private static Models.ModOption MapOption(CoreManifest.ModOption option) => new()
    {
        Name = option.Name,
        Description = option.Description,
        Include = option.Include,
        Image = option.Image,
        SubOptions = option.SubOptions?.Select(MapSubOption).ToArray(),
    };

    private static Models.ModSubOption MapSubOption(CoreManifest.ModSubOption option) => new()
    {
        Name = option.Name,
        Description = option.Description,
        Include = option.Include,
        Image = option.Image,
    };
}
