using System.IO;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.Models;

public sealed class ModItem : ObservableObject
{
    private bool _isEnabled;
    private bool _isSelected;
    private Guid? _groupId;
    private string? _groupName;
    private string _tagSummary = string.Empty;

    public ModItem(DiscoveredMod source)
    {
        Source = source;
    }

    public DiscoveredMod Source { get; }

    public Guid Id => Source.Manifest.Guid;

    public string Name => Source.Manifest.Name;

    public string Description => Source.Manifest.Description;

    public string? IconPath => Source.Manifest.IconPath is null
        ? null
        : Path.Combine(Source.Directory.FullName, Source.Manifest.IconPath);

    public string Version => Source.Manifest.Version.ToString();

    public DirectoryInfo Directory => Source.Directory;

    public IReadOnlyList<string> OptionNames => Source.Manifest switch
    {
        LegacyModManifest manifest => manifest.Options ?? [],
        V1ModManifest manifest => (manifest.Options ?? []).Select(option => option.Name).ToArray(),
        _ => [],
    };

    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>所属分组；<c>null</c> 表示未分组。分组名由库服务在加载/变更时一并写入。</summary>
    public Guid? GroupId
    {
        get => _groupId;
        set
        {
            if (SetProperty(ref _groupId, value) && value is null)
            {
                GroupName = null;
            }
        }
    }

    public string? GroupName { get => _groupName; private set => SetProperty(ref _groupName, value); }

    public string TagSummary { get => _tagSummary; internal set => SetProperty(ref _tagSummary, value); }

    public void SetGroup(Guid? groupId, string? groupName)
    {
        _groupId = groupId;
        GroupName = groupName;
        OnPropertyChanged(nameof(GroupId));
    }

    public List<Guid> TagIds { get; set; } = [];

    public List<bool> EnabledOptions { get; set; } = [];
    public List<int> SelectedOptions { get; set; } = [];
    public int SortOrder { get; set; }

    public IReadOnlyList<ModOption> V1Options => Source.Manifest is V1ModManifest manifest
        ? manifest.Options ?? []
        : [];

    public IReadOnlyList<string> LegacyOptionNames => Source.Manifest is LegacyModManifest manifest
        ? manifest.Options ?? []
        : [];

    public ModRuntimeState CreateRuntimeState() => new([.. EnabledOptions], [.. SelectedOptions], TagIds);

    public ModDeploymentInput CreateDeploymentInput()
    {
        var runtime = CreateRuntimeOptions();
        return new(Id, Directory, Source.Manifest, runtime.EnabledOptions, runtime.SelectedOptions);
    }

    private (List<bool> EnabledOptions, List<int> SelectedOptions) CreateRuntimeOptions()
    {
        return Source.Manifest switch
        {
            LegacyModManifest legacy => (CreateEnabled(legacy.Options?.Count ?? 0), CreateSelected(legacy.Options?.Count ?? 0)),
            V1ModManifest v1 => (CreateEnabled(v1.Options?.Count ?? 0), CreateSelected(v1.Options?.Count ?? 0)),
            _ => ([], []),
        };

        List<bool> CreateEnabled(int count) =>
            Enumerable.Range(0, count).Select(index => index < EnabledOptions.Count && EnabledOptions[index]).ToList();

        List<int> CreateSelected(int count) => Enumerable.Range(0, count).Select(index =>
        {
            if (index < SelectedOptions.Count)
            {
                return SelectedOptions[index];
            }

            return Source.Manifest is V1ModManifest manifest
                ? Math.Max(0, (manifest.Options![index].SubOptions ?? []).ToList().FindIndex(sub => !string.IsNullOrWhiteSpace(sub.Name)))
                : 0;
        }).ToList();
    }
}
