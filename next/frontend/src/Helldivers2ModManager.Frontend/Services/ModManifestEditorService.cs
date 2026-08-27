using System.IO;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ManifestEditDraft(
    string Name,
    string Description,
    string? IconPath,
    IReadOnlyList<CreateModOption> Options);

public sealed class ModManifestEditorService
{
    public ManifestEditDraft CreateDraft(ModItem item)
    {
        var options = item.Source.Manifest switch
        {
            V1ModManifest manifest => manifest.Options?.Select(option => new CreateModOption(
                option.Name,
                option.Description,
                option.Include ?? [],
                option.Image,
                option.SubOptions?.Select(sub => new CreateModSubOption(
                    sub.Name,
                    sub.Description,
                    sub.Include ?? [],
                    sub.Image)).ToArray() ?? [])).ToArray() ?? [],
            LegacyModManifest manifest => manifest.Options?.Select(name => new CreateModOption(
                name,
                string.Empty,
                [name],
                null,
                [])).ToArray() ?? [],
            _ => [],
        };
        return new(item.Name, item.Description, item.Source.Manifest.IconPath, options);
    }

    public async Task<bool> SaveAsync(
        ModItem item,
        ManifestEditDraft draft,
        CancellationToken cancellationToken = default)
    {
        var current = item.Source.Manifest;
        var directory = item.Directory;
        Directory.CreateDirectory(directory.FullName);
        var icon = await CopyImageAsync(draft.IconPath, directory, cancellationToken).ConfigureAwait(false);
        var options = new List<PreparedManifestOption>();
        foreach (var option in draft.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Name))
            {
                continue;
            }

            var image = await CopyImageAsync(option.ImagePath, directory, cancellationToken).ConfigureAwait(false);
            var subOptions = new List<ModSubOption>();
            foreach (var subOption in option.SubOptions)
            {
                if (string.IsNullOrWhiteSpace(subOption.Name))
                {
                    continue;
                }

                subOptions.Add(new ModSubOption(
                    subOption.Name.Trim(),
                    subOption.Description,
                    subOption.IncludePaths,
                    await CopyImageAsync(subOption.ImagePath, directory, cancellationToken).ConfigureAwait(false)));
            }

            options.Add(new PreparedManifestOption(
                option.Name.Trim(),
                option.Description,
                option.IncludePaths.Count > 0 ? option.IncludePaths : null,
                image,
                subOptions.Count > 0 ? subOptions.ToArray() : null));
        }

        var requiresV1 = current.Version == ManifestVersion.V1 || options.Any(option =>
            !string.IsNullOrWhiteSpace(option.Description) ||
            option.Image is not null ||
            option.SubOptions is not null ||
            option.Include is not null && option.Include.Count > 1);

        IModManifest manifest = requiresV1
            ? new V1ModManifest
            {
                Guid = current.Guid,
                Name = draft.Name,
                Description = draft.Description,
                IconPath = icon,
                Options = options.Count > 0
                    ? options.Select(option => new ModOption(
                        option.Name,
                        option.Description,
                        option.Include,
                        option.Image,
                        option.SubOptions)).ToArray()
                    : null,
                NexusData = current is V1ModManifest v1 ? v1.NexusData : null,
            }
            : new LegacyModManifest
            {
                Guid = current.Guid,
                Name = draft.Name,
                Description = draft.Description,
                IconPath = icon,
                Options = options.Count > 0 ? [.. options.Select(option => option.Name)] : null,
            };

        await Task.Run(() => ModManifest.SaveToFile(manifest, directory), cancellationToken).ConfigureAwait(false);
        return manifest.Version == ManifestVersion.V1 && current.Version != ManifestVersion.V1;
    }

    private sealed record PreparedManifestOption(
        string Name,
        string Description,
        IReadOnlyList<string>? Include,
        string? Image,
        IReadOnlyList<ModSubOption>? SubOptions);

    private static async Task<string?> CopyImageAsync(
        string? imagePath,
        DirectoryInfo modDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(imagePath, modDirectory.FullName);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        if (!fullPath.StartsWith(modDirectory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var target = Path.Combine(modDirectory.FullName, Path.GetFileName(fullPath));
            if (!string.Equals(fullPath, target, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => File.Copy(fullPath, target, true), cancellationToken).ConfigureAwait(false);
            }

            fullPath = target;
        }

        return Path.GetFileName(fullPath);
    }
}
