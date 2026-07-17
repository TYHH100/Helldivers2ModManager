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

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    private bool[] _enabledOptions = manifest.Version switch
    {
        ManifestVersion.Legacy => [],
        ManifestVersion.V1 => Enumerable.Repeat(true, ((V1ModManifest)manifest).Options is null ? 0 : ((V1ModManifest)manifest).Options!.Count).ToArray(),
        ManifestVersion.V2 => throw new NotSupportedException(),
        _ => throw new NotImplementedException()
    };
    public bool[] EnabledOptions
    {
        get => _enabledOptions;
        private set
        {
            _enabledOptions = value;
            OnPropertyChanged(nameof(EnabledOptions));
        }
    }

    private int[] _selectedOptions = manifest.Version switch
    {
        ManifestVersion.Legacy => new int[1],
        ManifestVersion.V1 => new int[((V1ModManifest)manifest).Options is null ? 0 : ((V1ModManifest)manifest).Options!.Count],
        ManifestVersion.V2 => throw new NotSupportedException(),
        _ => throw new NotImplementedException()
    };
    public int[] SelectedOptions
    {
        get => _selectedOptions;
        private set
        {
            _selectedOptions = value;
            OnPropertyChanged(nameof(SelectedOptions));
        }
    }

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

        // 获取当前清单期望的选项数量，用于适配保存数据的数组长度
        int expectedOptionCount = Manifest.Version switch
        {
            ManifestVersion.Legacy => 0,
            ManifestVersion.V1 => ((V1ModManifest)Manifest).Options?.Count ?? 0,
            _ => throw new NotImplementedException()
        };

        // 适配 Toggled 数组长度：若保存的数据长度与当前清单不匹配（如从无Options升级到有Options），
        // 则自动截断或填充默认值（true=启用），确保运行时数组长度始终与清单一致
        if (data.Toggled.Length == expectedOptionCount)
        {
            EnabledOptions = data.Toggled;
        }
        else
        {
            var toggled = new bool[expectedOptionCount];
            for (int i = 0; i < expectedOptionCount; i++)
                toggled[i] = i < data.Toggled.Length ? data.Toggled[i] : true;
            EnabledOptions = toggled;
        }

        // 适配 Selected 数组长度：新位置默认选中第一个子选项（索引0）
        // 注意：Legacy 特殊处理，其 Selected 数组始终为长度 1（供 LegacySelectedOption 访问索引0）
        int expectedSelectedCount = Manifest.Version switch
        {
            ManifestVersion.Legacy => 1,
            ManifestVersion.V1 => expectedOptionCount,
            _ => throw new NotImplementedException()
        };
        if (data.Selected.Length == expectedSelectedCount)
        {
            SelectedOptions = data.Selected;
        }
        else
        {
            var selected = new int[expectedSelectedCount];
            for (int i = 0; i < expectedSelectedCount; i++)
                selected[i] = i < data.Selected.Length ? data.Selected[i] : 0;
            SelectedOptions = selected;
        }

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