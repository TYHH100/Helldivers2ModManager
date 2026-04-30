using System.ComponentModel;
using System.IO;

namespace Helldivers2ModManager.Models;

internal sealed class ModData(DirectoryInfo dir, IModManifest manifest) : INotifyPropertyChanged
{
    public DirectoryInfo Directory { get; } = dir;

    private IModManifest _manifest = manifest;
    public IModManifest Manifest
    {
        get => _manifest;
        set
        {
            if (_manifest == value)
                return;
            _manifest = value;
            OnPropertyChanged(nameof(Manifest));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public bool Enabled { get; set; } = true;

    public bool[] EnabledOptions { get; private set; } = manifest.Version switch
    {
        ManifestVersion.Legacy => [],
        ManifestVersion.V1 => Enumerable.Repeat(true, ((V1ModManifest)manifest).Options is null ? 0 : ((V1ModManifest)manifest).Options!.Count).ToArray(),
        ManifestVersion.V2 => throw new NotSupportedException(),
        _ => throw new NotImplementedException()
    };

    public int[] SelectedOptions { get; private set; } = manifest.Version switch
    {
        ManifestVersion.Legacy => new int[1],
        ManifestVersion.V1 => new int[((V1ModManifest)manifest).Options is null ? 0 : ((V1ModManifest)manifest).Options!.Count],
        ManifestVersion.V2 => throw new NotSupportedException(),
        _ => throw new NotImplementedException()
    };

    public Guid? GroupId { get; set; }

	private List<Guid> _tagIds = [];
    public List<Guid> TagIds
    {
        get => _tagIds;
        set
        {
            if (_tagIds != value)
            {
                _tagIds = value;
                OnPropertyChanged(nameof(TagIds));
            }
        }
    }

    public void ApplyData(in EnabledData data)
    {
        Enabled = data.Enabled;
        EnabledOptions = data.Toggled;
		SelectedOptions = data.Selected;
        GroupId = data.GroupId;
        TagIds = data.TagIds?.ToList() ?? [];
    }

    public EnabledData ToEnabledData()
    {
        return new EnabledData
        {
            Guid = Manifest.Guid,
            Enabled = Enabled,
            Toggled = EnabledOptions,
            Selected = SelectedOptions,
            GroupId = GroupId,
            TagIds = TagIds,
        };
    }

    public void UpdateManifestName(string newName)
    {
        Manifest = Manifest.Version switch
        {
            ManifestVersion.Legacy => new LegacyModManifest
            {
                Guid = Manifest.Guid,
                Name = newName,
                Description = Manifest.Description,
                IconPath = Manifest.IconPath,
                Options = ((LegacyModManifest)Manifest).Options,
            },
            ManifestVersion.V1 => new V1ModManifest
            {
                Guid = Manifest.Guid,
                Name = newName,
                Description = Manifest.Description,
                IconPath = Manifest.IconPath,
                Options = ((V1ModManifest)Manifest).Options,
            },
            _ => throw new NotImplementedException()
        };
        ModManifest.SaveToFile(Manifest, Directory);
    }

    public void UpdateManifestDescription(string newDescription)
    {
        Manifest = Manifest.Version switch
        {
            ManifestVersion.Legacy => new LegacyModManifest
            {
                Guid = Manifest.Guid,
                Name = Manifest.Name,
                Description = newDescription,
                IconPath = Manifest.IconPath,
                Options = ((LegacyModManifest)Manifest).Options,
            },
            ManifestVersion.V1 => new V1ModManifest
            {
                Guid = Manifest.Guid,
                Name = Manifest.Name,
                Description = newDescription,
                IconPath = Manifest.IconPath,
                Options = ((V1ModManifest)Manifest).Options,
            },
            _ => throw new NotImplementedException()
        };
        ModManifest.SaveToFile(Manifest, Directory);
    }

    public void UpdateManifestIconPath(string? newIconPath)
    {
        Manifest = Manifest.Version switch
        {
            ManifestVersion.Legacy => new LegacyModManifest
            {
                Guid = Manifest.Guid,
                Name = Manifest.Name,
                Description = Manifest.Description,
                IconPath = newIconPath,
                Options = ((LegacyModManifest)Manifest).Options,
            },
            ManifestVersion.V1 => new V1ModManifest
            {
                Guid = Manifest.Guid,
                Name = Manifest.Name,
                Description = Manifest.Description,
                IconPath = newIconPath,
                Options = ((V1ModManifest)Manifest).Options,
            },
            _ => throw new NotImplementedException()
        };
        ModManifest.SaveToFile(Manifest, Directory);
    }
}