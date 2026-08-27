namespace Helldivers2ModManager.Core.Mods;

public static class ModPatchSelection
{
    public static IReadOnlyList<FileInfo> GetSelectedPatchFiles(
        DirectoryInfo modDirectory,
        IModManifest manifest,
        IReadOnlyList<bool> enabledOptions,
        IReadOnlyList<int> selectedOptions)
    {
        ArgumentNullException.ThrowIfNull(modDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(enabledOptions);
        ArgumentNullException.ThrowIfNull(selectedOptions);

        var directories = new List<DirectoryInfo>();
        void AddDirectory(string relativePath)
        {
            var directory = new DirectoryInfo(Path.Combine(modDirectory.FullName, relativePath));
            if (directory.Exists) directories.Add(directory);
        }

        switch (manifest)
        {
            case LegacyModManifest legacy:
                if (legacy.Options is { } legacyOptions)
                {
                    var selected = selectedOptions.Count > 0 ? selectedOptions[0] : 0;
                    if (selected >= 0 && selected < legacyOptions.Count) AddDirectory(legacyOptions[selected]);
                }
                else directories.Add(modDirectory);
                break;

            case V1ModManifest v1:
                if (v1.Options is not { } options)
                {
                    directories.Add(modDirectory);
                    break;
                }
                for (var index = 0; index < options.Count; index++)
                {
                    if (index >= enabledOptions.Count || !enabledOptions[index]) continue;
                    var option = options[index];
                    if (option.Include is { } includes)
                        foreach (var include in includes) AddDirectory(include);
                    if (option.SubOptions is not { } subOptions || subOptions.Count == 0) continue;
                    var selectedSub = index < selectedOptions.Count ? selectedOptions[index] : 0;
                    if (selectedSub >= 0 && selectedSub < subOptions.Count)
                        foreach (var include in subOptions[selectedSub].Include) AddDirectory(include);
                }
                break;
            default: throw new NotSupportedException("Unknown manifest version!");
        }

        return directories
            .SelectMany(directory => directory.GetFiles().Where(file => PatchFileRules.IsMainPatchFile(file.Name)))
            .GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }
}
