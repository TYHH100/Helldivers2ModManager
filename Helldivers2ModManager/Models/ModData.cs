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

            var previousManifest = _manifest;
            var previousEnabledOptions = _enabledOptions;
            var previousSelectedOptions = _selectedOptions;
            _manifest = value;
            SynchronizeOptionState(previousManifest, previousEnabledOptions, previousSelectedOptions);
            OnPropertyChanged(nameof(Manifest));
        }
    }

    /// <summary>
    /// Keeps runtime option state compatible when a manifest is edited or updated.
    /// In particular, a Legacy-to-V1 conversion must resize the arrays before the
    /// dashboard rebuilds option view models or saves the active profile.
    /// </summary>
    private void SynchronizeOptionState(IModManifest previousManifest, bool[] previousEnabledOptions, int[] previousSelectedOptions)
    {
        switch (Manifest)
        {
            case LegacyModManifest:
                EnabledOptions = [];
                SelectedOptions = [previousSelectedOptions.FirstOrDefault()];
                break;

            case V1ModManifest { Options: { } options }:
            {
                var enabled = Enumerable.Repeat(true, options.Count).ToArray();
                var selected = new int[options.Count];

                if (previousManifest.Version == ManifestVersion.V1)
                {
                    Array.Copy(previousEnabledOptions, enabled, Math.Min(previousEnabledOptions.Length, enabled.Length));
                    Array.Copy(previousSelectedOptions, selected, Math.Min(previousSelectedOptions.Length, selected.Length));
                }
                else if (previousSelectedOptions.Length > 0 && previousSelectedOptions[0] is var legacySelected
                    && legacySelected >= 0 && legacySelected < enabled.Length)
                {
                    // Legacy options form a mutually exclusive drop-down. Preserve the
                    // previously chosen entry when turning them into V1 option toggles.
                    Array.Fill(enabled, false);
                    enabled[legacySelected] = true;
                }

                EnabledOptions = enabled;
                SelectedOptions = selected;
                break;
            }

            case V1ModManifest:
                EnabledOptions = [];
                SelectedOptions = [];
                break;

            default:
                throw new NotSupportedException($"Unsupported manifest version: {Manifest.Version}");
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

    private bool? _isPhysBoneMod;

    /// <summary>
    /// 模组目录是否携带 HD2PhysBone 参数集。惰性探测并缓存（目录内容在运行期视为不变），
    /// 用于部署排序（PhysBone 模组置底最后部署）与参数目录生命周期。
    /// </summary>
    public bool IsPhysBoneMod
    {
        get => _isPhysBoneMod ??= Services.PhysBoneParamLocator.HasParamSet(Directory);
        internal set => _isPhysBoneMod = value;
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
                NexusData = ((V1ModManifest)Manifest).NexusData,
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
                NexusData = ((V1ModManifest)Manifest).NexusData,
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
                NexusData = ((V1ModManifest)Manifest).NexusData,
            },
            _ => throw new NotImplementedException()
        };
        ModManifest.SaveToFile(Manifest, Directory);
    }
}
