using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Core.Mods;

public static class ModManifestSanitizer
{
    public static IModManifest SanitizeImagePaths(IModManifest manifest, DirectoryInfo directory, ILogger? logger = null) => manifest switch
    {
        LegacyModManifest legacy when IsInvalidImagePath(legacy.IconPath, directory) => new LegacyModManifest
        {
            Guid = legacy.Guid,
            Name = legacy.Name,
            Description = legacy.Description,
            IconPath = null,
            Options = legacy.Options,
        },
        V1ModManifest v1 => SanitizeV1(v1, directory, logger),
        _ => manifest,
    };

    private static IModManifest SanitizeV1(V1ModManifest manifest, DirectoryInfo directory, ILogger? logger)
    {
        var iconChanged = IsInvalidImagePath(manifest.IconPath, directory);
        List<ModOption>? newOptions = null;
        var optionsChanged = false;

        if (manifest.Options is not null)
        {
            newOptions = [];
            foreach (var option in manifest.Options)
            {
                var optionImageChanged = IsInvalidImagePath(option.Image, directory);
                IReadOnlyList<ModSubOption>? newSubOptions = option.SubOptions;
                var subOptionsChanged = false;

                if (option.SubOptions is not null)
                {
                    var sanitizedSubOptions = new List<ModSubOption>(option.SubOptions.Count);
                    foreach (var subOption in option.SubOptions)
                    {
                        if (IsInvalidImagePath(subOption.Image, directory))
                        {
                            subOptionsChanged = true;
                            sanitizedSubOptions.Add(subOption with { Image = null });
                        }
                        else
                        {
                            sanitizedSubOptions.Add(subOption);
                        }
                    }

                    if (subOptionsChanged) newSubOptions = sanitizedSubOptions;
                }

                if (optionImageChanged || subOptionsChanged)
                {
                    optionsChanged = true;
                    newOptions.Add(option with
                    {
                        Image = optionImageChanged ? null : option.Image,
                        SubOptions = newSubOptions,
                    });
                }
                else
                {
                    newOptions.Add(option);
                }
            }
        }

        if (!iconChanged && !optionsChanged) return manifest;
        logger?.LogInformation("Sanitizing v1 manifest image paths for \"{Name}\"", manifest.Name);
        return manifest with
        {
            IconPath = iconChanged ? null : manifest.IconPath,
            Options = newOptions,
        };
    }

    public static bool IsInvalidImagePath(string? imagePath, DirectoryInfo root) =>
        string.IsNullOrWhiteSpace(imagePath)
            || !TryResolveManifestRelativePath(root, imagePath, out var fullPath)
            || !File.Exists(fullPath);

    public static bool TryResolveManifestRelativePath(DirectoryInfo root, string? relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        try
        {
            var rootPath = Path.GetFullPath(root.FullName);
            fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
            var prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            return fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
