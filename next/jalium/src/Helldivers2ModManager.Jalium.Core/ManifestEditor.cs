using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class ManifestEditor
{
    private readonly LocalizationService _localization;

    public ModData Mod { get; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string IconPath { get; set; }
    public bool IsV1 { get; set; }
    public List<ManifestOptionDraft> Options { get; } = [];
    public string? ExternalIconPath { get; private set; }

    public ManifestEditor(ModData mod, LocalizationService localization)
    {
        Mod = mod;
        _localization = localization;
        Name = mod.Manifest.Name;
        Description = mod.Manifest.Description;
        IconPath = mod.Manifest.IconPath ?? string.Empty;
        IsV1 = mod.Manifest.Version == ManifestVersion.V1;
        LoadOptions(mod.Manifest);
    }

    public void SetIconFromFile(string file)
    {
        ExternalIconPath = file;
        IconPath = Path.GetFileName(file);
    }

    public void SetIconPath(string path)
    {
        ExternalIconPath = null;
        IconPath = path;
    }

    public void SwitchFormat(bool v1)
    {
        if (IsV1 == v1)
            return;
        IsV1 = v1;
        foreach (var option in Options)
        {
            if (v1)
            {
                if (string.IsNullOrWhiteSpace(option.IncludePaths))
                    option.IncludePaths = option.Name;
            }
            else
            {
                var directory = option.IncludePaths.Split(';', StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries).FirstOrDefault();
                option.Name = string.IsNullOrWhiteSpace(directory) ? option.Name : directory;
                option.IncludePaths = option.Name;
                option.Description = string.Empty;
                option.ImagePath = string.Empty;
                option.SubOptions.Clear();
            }
        }
    }

    public void AddOption(string name = "") => Options.Add(new ManifestOptionDraft
    {
        Name = name,
        SourceDirectory = Mod.Directory.FullName,
    });

    public void AddLegacyOptions(IEnumerable<string> relativePaths)
    {
        foreach (var path in relativePaths)
            AddOption(path);
    }

    public void Save()
    {
        var icon = CopyExternalImage(ExternalIconPath ??
            (Path.IsPathRooted(IconPath) ? IconPath : null), IconPath);
        var current = Mod.Manifest;
        IModManifest updated;
        if (!IsV1)
        {
            updated = new LegacyModManifest
            {
                Guid = current.Guid,
                Name = Name,
                Description = Description,
                IconPath = icon,
                Options = Options.Where(option => !string.IsNullOrWhiteSpace(option.Name))
                    .Select(option => option.Name.Trim()).ToArray() is { Length: > 0 } values
                    ? values : null,
            };
        }
        else
        {
            var options = Options.Select(option => new ModOption
            {
                Name = string.IsNullOrWhiteSpace(option.Name)
                    ? _localization["CreateModOption.DefaultName"] : option.Name.Trim(),
                Description = option.Description,
                Include = ParsePaths(option.IncludePaths) is { Count: > 0 } includes ? includes : null,
                Image = CopyExternalImage(option.ExternalImagePath ??
                    (Path.IsPathRooted(option.ImagePath) ? option.ImagePath : null), option.ImagePath),
                SubOptions = option.SubOptions.Count == 0 ? null : option.SubOptions.Select(sub => new ModSubOption
                {
                    Name = string.IsNullOrWhiteSpace(sub.Name)
                        ? _localization["CreateSubOption.DefaultName"] : sub.Name.Trim(),
                    Description = sub.Description,
                    Include = ParsePaths(sub.IncludePaths),
                    Image = CopyExternalImage(sub.ExternalImagePath ??
                        (Path.IsPathRooted(sub.ImagePath) ? sub.ImagePath : null), sub.ImagePath),
                }).ToArray(),
            }).ToArray();
            updated = new V1ModManifest
            {
                Guid = current.Guid,
                Name = Name,
                Description = Description,
                IconPath = icon,
                Options = options.Length == 0 ? null : options,
                NexusData = (current as V1ModManifest)?.NexusData,
            };
        }

        Mod.Manifest = updated;
        ModManifest.SaveToFile(updated, Mod.Directory);
        ExternalIconPath = null;
        foreach (var option in Options)
        {
            option.ExternalImagePath = null;
            foreach (var sub in option.SubOptions)
                sub.ExternalImagePath = null;
        }
    }

    private void LoadOptions(IModManifest manifest)
    {
        if (manifest is V1ModManifest v1)
        {
            foreach (var option in v1.Options ?? [])
            {
                var draft = new ManifestOptionDraft
                {
                    SourceDirectory = Mod.Directory.FullName,
                    Name = option.Name,
                    Description = option.Description,
                    IncludePaths = string.Join(';', option.Include ?? []),
                    ImagePath = option.Image ?? string.Empty,
                };
                foreach (var sub in option.SubOptions ?? [])
                    draft.SubOptions.Add(new ManifestSubOptionDraft
                    {
                        Name = sub.Name,
                        Description = sub.Description,
                        IncludePaths = string.Join(';', sub.Include),
                        ImagePath = sub.Image ?? string.Empty,
                    });
                Options.Add(draft);
            }
        }
        else if (manifest is LegacyModManifest legacy)
            AddLegacyOptions(legacy.Options ?? []);
    }

    private string? CopyExternalImage(string? source, string relative)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.IsNullOrWhiteSpace(relative) ? null : relative;
        if (!File.Exists(source))
            return string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ? null : relative;
        var name = Path.GetFileName(source);
        var destination = Path.GetFullPath(Path.Combine(Mod.Directory.FullName, name));
        var root = Path.GetFullPath(Mod.Directory.FullName) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Icon path escaped the mod directory.");
        if (!File.Exists(destination))
            File.Copy(source, destination);
        return name;
    }

    internal static List<string> ParsePaths(string value) => value.Split(';',
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

internal sealed class ManifestOptionDraft
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IncludePaths { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string? ExternalImagePath { get; set; }
    public List<ManifestSubOptionDraft> SubOptions { get; } = [];

    public void SetImageFromFile(string file)
    {
        ExternalImagePath = file;
        ImagePath = Path.GetFileName(file);
    }
}

internal sealed class ManifestSubOptionDraft
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IncludePaths { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string? ExternalImagePath { get; set; }

    public void SetImageFromFile(string file)
    {
        ExternalImagePath = file;
        ImagePath = Path.GetFileName(file);
    }
}
