using Helldivers2ModManager.Models;

namespace Helldivers2ModManager.Jalium.Core;

internal static class ManifestPathValidator
{
    public static bool Check(IModManifest manifest, DirectoryInfo directory, List<ModProblem> problems)
    {
        var valid = true;

        void CheckImage(string? path)
        {
            if (path is null)
                return;
            if (string.IsNullOrWhiteSpace(path))
            {
                problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.EmptyImagePath, ExtraData = path });
                return;
            }
            if (!TryResolve(directory, path, out var fullPath) || !File.Exists(fullPath))
                problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.InvalidImagePath, ExtraData = path });
        }

        void CheckDirectory(string? path)
        {
            // Empty and missing option directories are valid placeholders in the current manifest rules.
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!TryResolve(directory, path, out _))
            {
                problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.InvalidPath, ExtraData = path });
                valid = false;
            }
        }

        CheckImage(manifest.IconPath);
        switch (manifest)
        {
            case LegacyModManifest { Options: { } options }:
                if (options.Count == 0)
                    problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.EmptyOptions });
                foreach (var option in options)
                    CheckDirectory(option);
                break;

            case V1ModManifest { Options: { } options }:
                if (options.Count == 0)
                    problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.EmptyOptions });
                if (options.Any(static option => option.SubOptions is { Count: 0 }))
                    problems.Add(new ModProblem { Directory = directory, Kind = ModProblemKind.EmptySubOptions });
                foreach (var option in options)
                {
                    CheckImage(option.Image);
                    if (option.Include is not null)
                        foreach (var path in option.Include)
                            CheckDirectory(path);
                    if (option.SubOptions is not null)
                        foreach (var subOption in option.SubOptions)
                        {
                            CheckImage(subOption.Image);
                            foreach (var path in subOption.Include)
                                CheckDirectory(path);
                        }
                }
                break;
        }

        return valid;
    }

    private static bool TryResolve(DirectoryInfo directory, string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (Path.IsPathRooted(path))
            return false;
        try
        {
            var root = Path.GetFullPath(directory.FullName);
            fullPath = Path.GetFullPath(Path.Combine(root, path));
            return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
