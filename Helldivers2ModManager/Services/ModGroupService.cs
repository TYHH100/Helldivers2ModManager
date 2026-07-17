using Helldivers2ModManager.Models;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class ModGroupService
{
    private readonly ILogger<ModGroupService> _logger;
    private readonly ModGroupRepository _repository;
    private readonly LocalizationService _localizationService;
    private readonly DatabaseService _databaseService;
    private readonly Dictionary<Guid, Dictionary<Guid, GroupedEnabledData>> _stateCache = [];
    private string _storageDirectory = string.Empty;
    private bool _initialized;
    private Guid _lastSelectedGroupId = ModGroup.DefaultGroupId;

    public ObservableCollection<ModGroup> Groups { get; } = [];

    public ModGroup SelectedGroup { get; private set; }

    public bool IsSidebarOpen { get; set; }

    public event EventHandler? SelectedGroupChanged;

    public ModGroupService(
        ILogger<ModGroupService> logger,
        ModGroupRepository repository,
        LocalizationService localizationService,
        DatabaseService databaseService)
    {
        _logger = logger;
        _repository = repository;
        _localizationService = localizationService;
        _databaseService = databaseService;
        SelectedGroup = CreateDefaultGroup();
    }

    public async Task InitAsync(SettingsService settingsService, IReadOnlyList<ModData> mods)
    {
        _storageDirectory = settingsService.StorageDirectory;
        Groups.Clear();
        var existingGuids = mods.Select(static mod => mod.Manifest.Guid).ToHashSet();
        var missingGuids = new HashSet<Guid>();

        var loadedGroups = _repository.LoadGroups(_storageDirectory)
            .Where(static group => !group.IsDefault)
            .OrderBy(static group => group.DisplayIndex)
            .ToList();

        var defaultGroup = CreateDefaultGroup();
        Groups.Add(defaultGroup);
        foreach (var group in loadedGroups)
        {
            RemoveMissingMembers(group, existingGuids, missingGuids);
            Groups.Add(group);
        }

        SelectedGroup = Groups.FirstOrDefault(group => group.Id == _lastSelectedGroupId) ?? defaultGroup;
        _stateCache.Clear();
        foreach (var group in Groups)
        {
            _stateCache[group.Id] = _repository.LoadStates(_storageDirectory, group.Id)
                .Where(state => KeepExistingState(state, existingGuids, missingGuids))
                .GroupBy(static state => state.Guid)
                .ToDictionary(static group => group.Key, static group => group.OrderBy(static state => state.SortOrder).First());
        }

        // enabled_mods 是默认组的权威来源。每次启动都用已加载的 Profile 刷新默认组缓存，
        // 避免上次写入中断造成 group_mod_states 反向覆盖较新的主页状态。
        CaptureGroupState(defaultGroup.Id, mods);
        if (!_databaseService.IsReadOnly)
        {
            if (missingGuids.Count > 0)
                await _repository.DeleteStatesByGuidsAsync(_storageDirectory, missingGuids);
            await SaveGroupStateAsync(defaultGroup.Id);
            await SaveGroupsAsync();
        }
        else
        {
            _logger.LogWarning("Group persistence is disabled because the database is in read-only recovery mode");
        }
        _initialized = true;
    }

    public IEnumerable<ModData> FilterMods(IEnumerable<ModData> mods)
    {
        if (!_initialized)
            return mods;
        if (SelectedGroup.IsDefault)
            return mods;

        var members = SelectedGroup.ModGuids.ToHashSet();
        return mods.Where(mod => members.Contains(mod.Manifest.Guid));
    }

    public IEnumerable<ModViewModel> FilterModViewModels(IEnumerable<ModViewModel> mods)
    {
        if (!_initialized)
            return mods;
        if (SelectedGroup.IsDefault)
            return mods;

        var members = SelectedGroup.ModGuids.ToHashSet();
        return mods.Where(mod => members.Contains(mod.Guid));
    }

    public async Task SelectGroupAsync(Guid groupId, IEnumerable<ModData> currentMods)
    {
        GuardInitialized();
        EnsureWritable();
        var target = Groups.FirstOrDefault(group => group.Id == groupId);
        if (target is null || target.Id == SelectedGroup.Id)
            return;

        CaptureGroupState(SelectedGroup.Id, currentMods);
        await SaveGroupStateAsync(SelectedGroup.Id);
        SelectedGroup = target;
        _lastSelectedGroupId = target.Id;
        ApplyGroupState(target.Id, currentMods);
        SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ModGroup> CreateGroupAsync(string name)
    {
        GuardInitialized();
        EnsureWritable();
        var trimmedName = NormalizeName(name);
        ValidateGroupName(trimmedName);

        var group = new ModGroup
        {
            Id = Guid.NewGuid(),
            Name = trimmedName,
            CreatedAtUtc = DateTime.UtcNow,
            DisplayIndex = Groups.Count,
        };
        Groups.Add(group);
        _stateCache[group.Id] = [];
        await SaveGroupsAsync();
        return group;
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        GuardInitialized();
        EnsureWritable();
        var group = Groups.FirstOrDefault(group => group.Id == groupId);
        if (group is null || group.IsDefault)
            return;

        Groups.Remove(group);
        _stateCache.Remove(group.Id);
        await _repository.DeleteGroupAsync(_storageDirectory, group.Id);
        await SaveGroupsAsync();

        if (SelectedGroup.Id == group.Id)
        {
            SelectedGroup = Groups.First(static group => group.IsDefault);
            _lastSelectedGroupId = SelectedGroup.Id;
            SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task AddModsToGroupAsync(Guid groupId, IEnumerable<ModData> mods)
    {
        GuardInitialized();
        EnsureWritable();
        var group = Groups.FirstOrDefault(group => group.Id == groupId);
        if (group is null || group.IsDefault)
            return;

        var added = false;
        foreach (var mod in mods)
        {
            var guid = mod.Manifest.Guid;
            if (group.ModGuids.Contains(guid))
                continue;

            group.ModGuids.Add(guid);
            CopyDefaultStateToGroup(group.Id, mod);
            added = true;
        }

        if (!added)
            return;

        await SaveGroupsAsync();
        await SaveGroupStateAsync(group.Id);
        SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveModsFromGroupAsync(Guid groupId, IEnumerable<ModData> mods)
    {
        GuardInitialized();
        EnsureWritable();
        var group = Groups.FirstOrDefault(group => group.Id == groupId);
        if (group is null || group.IsDefault)
            return;

        var removed = false;
        foreach (var mod in mods)
        {
            var guid = mod.Manifest.Guid;
            if (group.ModGuids.Remove(guid))
            {
                if (_stateCache.TryGetValue(group.Id, out var states))
                    states.Remove(guid);
                removed = true;
            }
        }

        if (!removed)
            return;

        await SaveGroupsAsync();
        await SaveGroupStateAsync(group.Id);
        SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveModsFromAllGroupsAsync(IEnumerable<Guid> guids)
    {
        GuardInitialized();
        EnsureWritable();
        var guidSet = guids.ToHashSet();
        if (guidSet.Count == 0)
            return;

        var groupsChanged = false;
        foreach (var group in Groups.Where(static group => !group.IsDefault))
        {
            for (int i = group.ModGuids.Count - 1; i >= 0; i--)
            {
                if (guidSet.Contains(group.ModGuids[i]))
                {
                    group.ModGuids.RemoveAt(i);
                    groupsChanged = true;
                }
            }
        }

        foreach (var states in _stateCache.Values)
        {
            foreach (var guid in guidSet)
                states.Remove(guid);
        }

        if (groupsChanged)
            await SaveGroupsAsync();
        await _repository.DeleteStatesByGuidsAsync(_storageDirectory, guidSet);
        SelectedGroupChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CaptureGroupState(Guid groupId, IEnumerable<ModData> mods)
    {
        if (!_stateCache.TryGetValue(groupId, out var states))
            states = _stateCache[groupId] = [];

        foreach (var (mod, index) in mods.Select(static (mod, index) => (mod, index)))
        {
            states[mod.Manifest.Guid] = new GroupedEnabledData
            {
                GroupId = groupId,
                Guid = mod.Manifest.Guid,
                Enabled = mod.Enabled,
                Toggled = mod.EnabledOptions.ToArray(),
                Selected = mod.SelectedOptions.ToArray(),
                SortOrder = index,
            };
        }
    }

    public void ApplyGroupState(Guid groupId, IEnumerable<ModData> mods)
    {
        if (!_stateCache.TryGetValue(groupId, out var states))
            states = _stateCache[groupId] = [];

        foreach (var mod in mods)
        {
            if (!IsModVisibleInGroup(groupId, mod.Manifest.Guid))
                continue;

            if (states.TryGetValue(mod.Manifest.Guid, out var state))
            {
                mod.ApplyData(new EnabledData
                {
                    Guid = state.Guid,
                    Enabled = state.Enabled,
                    Toggled = state.Toggled.ToArray(),
                    Selected = state.Selected.ToArray(),
                    TagIds = mod.TagIds.ToList(),
                });
            }
            else
            {
                CopyDefaultStateToGroup(groupId, mod);
                if (states.TryGetValue(mod.Manifest.Guid, out var copiedState))
                {
                    mod.ApplyData(new EnabledData
                    {
                        Guid = copiedState.Guid,
                        Enabled = copiedState.Enabled,
                        Toggled = copiedState.Toggled.ToArray(),
                        Selected = copiedState.Selected.ToArray(),
                        TagIds = mod.TagIds.ToList(),
                    });
                }
            }
        }
    }

    public async Task SaveSelectedGroupStateAsync(IEnumerable<ModData> mods)
    {
        GuardInitialized();
        EnsureWritable();
        CaptureGroupState(SelectedGroup.Id, FilterMods(mods));
        await SaveGroupStateAsync(SelectedGroup.Id);
    }

    public async Task SaveGroupSnapshotAsync(ProfileSnapshot snapshot)
    {
        GuardInitialized();
        EnsureWritable();
        var states = snapshot.ToGroupedEnabledData()
            .ToDictionary(static state => state.Guid);
        _stateCache[snapshot.GroupId] = states;
        await _repository.SaveStatesAsync(
            _storageDirectory,
            snapshot.GroupId,
            states.Values.OrderBy(static state => state.SortOrder)).ConfigureAwait(false);
    }

    public int GetMemberCount(ModGroup group, int totalModCount)
    {
        return group.IsDefault ? totalModCount : group.ModGuids.Count;
    }

    private async Task SaveGroupsAsync()
    {
        await _repository.SaveGroupsAsync(_storageDirectory, Groups.Where(static group => !group.IsDefault));
    }

    private async Task SaveGroupStateAsync(Guid groupId)
    {
        if (!_stateCache.TryGetValue(groupId, out var states))
            return;

        await _repository.SaveStatesAsync(_storageDirectory, groupId, states.Values.OrderBy(static state => state.SortOrder));
    }

    private void EnsureWritable() => _databaseService.EnsureWritable(_storageDirectory);

    private bool IsModVisibleInGroup(Guid groupId, Guid modGuid)
    {
        if (groupId == ModGroup.DefaultGroupId)
            return true;

        var group = Groups.FirstOrDefault(group => group.Id == groupId);
        return group is not null && group.ModGuids.Contains(modGuid);
    }

    private void CopyDefaultStateToGroup(Guid groupId, ModData mod)
    {
        if (!_stateCache.TryGetValue(groupId, out var targetStates))
            targetStates = _stateCache[groupId] = [];

        if (!_stateCache.TryGetValue(ModGroup.DefaultGroupId, out var defaultStates)
            || !defaultStates.TryGetValue(mod.Manifest.Guid, out var sourceState))
        {
            sourceState = new GroupedEnabledData
            {
                GroupId = ModGroup.DefaultGroupId,
                Guid = mod.Manifest.Guid,
                Enabled = mod.Enabled,
                Toggled = mod.EnabledOptions.ToArray(),
                Selected = mod.SelectedOptions.ToArray(),
                SortOrder = targetStates.Count,
            };
        }

        targetStates[mod.Manifest.Guid] = new GroupedEnabledData
        {
            GroupId = groupId,
            Guid = mod.Manifest.Guid,
            Enabled = sourceState.Enabled,
            Toggled = sourceState.Toggled.ToArray(),
            Selected = sourceState.Selected.ToArray(),
            SortOrder = targetStates.Count,
        };
    }

    private void RemoveMissingMembers(ModGroup group, HashSet<Guid> existingGuids, HashSet<Guid> missingGuids)
    {
        for (int i = group.ModGuids.Count - 1; i >= 0; i--)
        {
            if (!existingGuids.Contains(group.ModGuids[i]))
            {
                _logger.LogWarning("分组 {GroupName} 中的 Mod 已不存在，移除成员: {Guid}", group.Name, group.ModGuids[i]);
                missingGuids.Add(group.ModGuids[i]);
                group.ModGuids.RemoveAt(i);
            }
        }
    }

    private bool KeepExistingState(GroupedEnabledData state, HashSet<Guid> existingGuids, HashSet<Guid> missingGuids)
    {
        if (existingGuids.Contains(state.Guid))
            return true;

        missingGuids.Add(state.Guid);
        return false;
    }

    private ModGroup CreateDefaultGroup()
    {
        return new ModGroup
        {
            Id = ModGroup.DefaultGroupId,
            Name = _localizationService["ModGroup.DefaultName"],
            CreatedAtUtc = DateTime.UnixEpoch,
            DisplayIndex = 0,
        };
    }

    private void ValidateGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(_localizationService["ModGroup.NameEmpty"]);
        if (name.Length > 40)
            throw new ArgumentException(_localizationService["ModGroup.NameTooLong"]);
        if (Groups.Any(group => string.Equals(group.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException(_localizationService["ModGroup.NameDuplicate"]);
    }

    private static string NormalizeName(string name)
    {
        return name.Trim();
    }

    private void GuardInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Mod group service has not been initialized.");
    }
}
