using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using ToolGood.Words.Pinyin;

namespace Helldivers2ModManager.Jalium.Core;

internal enum DashboardCatalogMode
{
    Workspace,
    Library,
}

internal sealed class DashboardWorkspace
{
    private readonly SettingsService _settings;
    private readonly DashboardLibraryService _library;
    private readonly ModGroupService _groups;
    private readonly ModGroupRepository _groupRepository;
    private readonly Dictionary<Guid, (string Full, string Initials)> _pinyin = [];
    private readonly HashSet<Guid> _selected = [];
    private List<ModData> _mods;
    private IReadOnlyList<object> _rows = [];
    private IReadOnlyList<ModProblem> _problems;
    private string _searchText = string.Empty;
    private DashboardCatalogMode _mode;

    public event EventHandler? Changed;
    public event EventHandler? ProfileChanged;

    public IReadOnlyList<ModData> Mods => _mods;
    public IReadOnlyList<object> Rows => _rows;
    public IReadOnlyList<ModProblem> Problems => _problems;
    public IReadOnlyCollection<Guid> SelectedGuids => _selected;
    public ModGroupService Groups => _groups;
    public DashboardCatalogMode Mode => _mode;
    public string SearchText => _searchText;
    public bool UseDeploymentOrder => _settings.UseDeploymentOrder;
    public bool EnableBatchRepair => _settings.EnableBatchRepair;
    public bool ShowSeparator => _settings.ShowSeparator;
    public float CardOpacity => _settings.CardOpacity;
    public bool HasSelection => SelectableMods().Any(mod => _selected.Contains(mod.Manifest.Guid));
    public int SelectedCount => SelectableMods().Count(mod => _selected.Contains(mod.Manifest.Guid));

    public int GetPosition(ModData mod)
    {
        var position = 1;
        foreach (var item in SelectableMods())
        {
            if (ReferenceEquals(item, mod))
                return position;
            position++;
        }
        return 0;
    }

    public IReadOnlyList<ModTag> GetTags(ModData mod)
    {
        var ids = mod.TagIds.ToHashSet();
        return _settings.Tags.Where(tag => ids.Contains(tag.Id)).ToArray();
    }

    public IReadOnlyList<ModData> SelectedMods => SelectableMods()
        .Where(mod => _selected.Contains(mod.Manifest.Guid)).ToArray();

    public ProfileSnapshot CaptureProfileSnapshot()
    {
        var group = _groups.SelectedGroup;
        var mods = _groups.FilterMods(_mods).ToArray();
        return ProfileSnapshot.Capture(0, group.Id, group.IsDefault, mods,
            mods.Select(mod => mod.Manifest.Guid));
    }

    public void RefreshTags() => RebuildRows();

    public async Task<int> ApplyAutoTagsAsync(
        ModTypeDetectionService detectionService,
        LocalizationService localization,
        IReadOnlyDictionary<string, ModTypeDetectionService.ModTypeDetectionResult> detections,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_settings.IsReadonly || !_settings.EnableAutoTagging || _mods.Count == 0)
            return 0;

        var originalTags = _settings.Tags.ToArray();
        var originalIds = _mods.Select(mod => mod.TagIds.ToList()).ToArray();
        var changed = detectionService.ApplyAutoTags(_settings, localization, _mods, detections,
            _settings.AutoTagCreateMissingTags, saveCreatedTags: false);
        if (changed == 0)
            return 0;

        var createdTags = _settings.Tags.Count != originalTags.Length;
        try
        {
            if (createdTags)
                await _settings.SaveAsync();
            await _library.SaveTagsAsync(_settings, _mods);
        }
        catch
        {
            for (var index = 0; index < _mods.Count; index++)
                _mods[index].TagIds = originalIds[index];
            if (createdTags)
            {
                _settings.Tags.Clear();
                foreach (var tag in originalTags)
                    _settings.Tags.Add(tag);
                await _settings.SaveAsync();
            }
            throw;
        }

        RebuildRows();
        return changed;
    }

    public async Task SetTagsAsync(IReadOnlyList<ModData> mods, IReadOnlyCollection<Guid> tagIds)
    {
        if (_settings.IsReadonly)
            throw new InvalidOperationException("Tag settings are not writable.");
        var available = _settings.Tags.Select(tag => tag.Id).ToHashSet();
        if (tagIds.Any(id => !available.Contains(id)))
            throw new ArgumentException("Unknown tag ID.", nameof(tagIds));
        var previous = mods.Select(mod => mod.TagIds).ToArray();
        foreach (var mod in mods)
            mod.TagIds = tagIds.ToList();
        try { await _library.SaveTagsAsync(_settings, mods); }
        catch
        {
            for (var index = 0; index < mods.Count; index++)
                mods[index].TagIds = previous[index];
            throw;
        }
        RebuildRows();
    }

    private DashboardWorkspace(
        SettingsService settings,
        DashboardLibraryService library,
        ModGroupService groups,
        ModGroupRepository groupRepository,
        ModCatalogResult loaded)
    {
        _settings = settings;
        _library = library;
        _groups = groups;
        _groupRepository = groupRepository;
        _mods = loaded.Mods.ToList();
        _problems = loaded.Problems;
    }

    public static async Task<DashboardWorkspace> OpenAsync(
        SettingsService settings,
        DashboardLibraryService library,
        ModGroupService groups,
        ModGroupRepository groupRepository,
        CancellationToken cancellationToken = default)
    {
        var loaded = await library.LoadAsync(settings, cancellationToken);
        await groups.InitAsync(settings, loaded.Mods);
        groups.ApplyGroupState(groups.SelectedGroup.Id, loaded.Mods);
        var workspace = new DashboardWorkspace(settings, library, groups, groupRepository, loaded);
        await workspace.WarmSearchCacheAsync(cancellationToken);
        workspace.RebuildRows();
        return workspace;
    }

    public void SetMode(DashboardCatalogMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        RebuildRows();
    }

    public void SetSearchText(string? searchText)
    {
        searchText ??= string.Empty;
        if (_searchText == searchText)
            return;
        _searchText = searchText;
        RebuildRows();
    }

    public void SelectAll() => SetSelection(_ => true);
    public void DeselectAll() => SetSelection(_ => false);
    public void InvertSelection() => SetSelection(mod => !_selected.Contains(mod.Manifest.Guid));

    public void SetSelected(ModData mod, bool selected)
    {
        if (selected)
            _selected.Add(mod.Manifest.Guid);
        else
            _selected.Remove(mod.Manifest.Guid);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceVisibleSelection(IEnumerable<Guid> selectedGuids)
    {
        var selected = selectedGuids.ToHashSet();
        foreach (var mod in _rows.OfType<ModData>())
            if (selected.Contains(mod.Manifest.Guid))
                _selected.Add(mod.Manifest.Guid);
            else
                _selected.Remove(mod.Manifest.Guid);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SelectRange(ModData anchor, ModData target, bool additive)
    {
        var visible = SelectableMods().ToArray();
        var first = Array.IndexOf(visible, anchor);
        var last = Array.IndexOf(visible, target);
        if (!additive)
            _selected.Clear();
        if (first < 0 || last < 0)
            _selected.Add(target.Manifest.Guid);
        else
            for (var index = Math.Min(first, last); index <= Math.Max(first, last); index++)
                _selected.Add(visible[index].Manifest.Guid);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetEnabledAsync(ModData mod, bool enabled)
    {
        if (_mode != DashboardCatalogMode.Workspace || !_groups.FilterMods(_mods).Contains(mod))
            return;
        var previous = mod.Enabled;
        mod.Enabled = enabled;
        try
        {
            await SaveCurrentAsync();
        }
        catch
        {
            mod.Enabled = previous;
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSelectedEnabledAsync(bool enabled)
    {
        if (_mode != DashboardCatalogMode.Workspace)
            return;
        var selected = _groups.FilterMods(_mods)
            .Where(mod => _selected.Contains(mod.Manifest.Guid)).ToArray();
        var previous = selected.Select(mod => mod.Enabled).ToArray();
        foreach (var mod in selected)
            mod.Enabled = enabled;
        try
        {
            await SaveCurrentAsync();
        }
        catch
        {
            for (var index = 0; index < selected.Length; index++)
                selected[index].Enabled = previous[index];
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetLegacyOptionAsync(ModData mod, int selectedIndex)
    {
        if (_mode != DashboardCatalogMode.Workspace
            || mod.Manifest is not LegacyModManifest { Options: { } options }
            || selectedIndex < 0 || selectedIndex >= options.Count
            || !_groups.FilterMods(_mods).Contains(mod))
            return;
        var previous = mod.SelectedOptions[0];
        mod.SelectedOptions[0] = selectedIndex;
        try
        {
            await SaveCurrentAsync();
        }
        catch
        {
            mod.SelectedOptions[0] = previous;
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SelectGroupAsync(Guid groupId)
    {
        await SaveCurrentAsync();
        await _groups.SelectGroupAsync(groupId, _mods);
        _selected.Clear();
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ModGroup> CreateGroupAsync(string name)
    {
        var group = await _groups.CreateGroupAsync(name);
        Changed?.Invoke(this, EventArgs.Empty);
        return group;
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        await _groups.DeleteGroupAsync(groupId);
        _selected.Clear();
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AddToGroupAsync(Guid groupId, IEnumerable<ModData> mods)
    {
        await _groups.AddModsToGroupAsync(groupId, mods);
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetModsToGroupsAsync(IReadOnlyCollection<Guid> groupIds, IReadOnlyList<ModData> mods)
    {
        if (mods.Count == 0)
            return;
        var selectedGroups = _groups.Groups.Where(group => groupIds.Contains(group.Id)).ToArray();
        if (_mode == DashboardCatalogMode.Workspace)
            await _groups.RemoveModsFromAllGroupsAsync(mods.Select(mod => mod.Manifest.Guid));
        foreach (var group in selectedGroups)
            await _groups.AddModsToGroupAsync(group.Id, mods);
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveSelectedFromCurrentGroupAsync()
    {
        await _groups.RemoveModsFromGroupAsync(_groups.SelectedGroup.Id,
            _groups.FilterMods(_mods).Where(mod => _selected.Contains(mod.Manifest.Guid)).ToArray());
        _selected.Clear();
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveFromCurrentGroupAsync(ModData mod)
    {
        if (_mode != DashboardCatalogMode.Workspace)
            return;
        await _groups.RemoveModsFromGroupAsync(_groups.SelectedGroup.Id, [mod]);
        _selected.Remove(mod.Manifest.Guid);
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MoveAsync(ModData source, int insertIndex)
    {
        if (_mode != DashboardCatalogMode.Workspace || !string.IsNullOrEmpty(_searchText))
            return;
        var visible = _groups.FilterMods(_mods).ToList();
        if (!visible.Contains(source))
            return;
        var oldOrder = _mods.ToArray();
        var group = _groups.SelectedGroup;
        var oldMembers = group.ModGuids.ToArray();
        var separators = _rows.OfType<ModSeparator>().ToArray();
        var oldSeparatorPositions = separators.Select(separator => separator.DisplayIndex).ToArray();
        var moving = _selected.Contains(source.Manifest.Guid)
            ? visible.Where(mod => _selected.Contains(mod.Manifest.Guid)).ToArray()
            : [source];
        var target = Math.Clamp(insertIndex, 0, visible.Count);
        var rows = _rows.ToList();
        foreach (var mod in moving)
            rows.Remove(mod);
        var remainingMods = rows.OfType<ModData>().ToArray();
        target -= moving.Count(mod => visible.IndexOf(mod) < target);
        var insertAt = target >= remainingMods.Length ? rows.Count : rows.IndexOf(remainingMods[target]);
        rows.InsertRange(insertAt, moving);
        visible = rows.OfType<ModData>().ToList();
        foreach (var separator in separators)
            separator.DisplayIndex = rows.IndexOf(separator);

        try
        {
            await _settings.SaveAsync();
            if (group.IsDefault)
            {
                var moved = visible.Select(mod => mod.Manifest.Guid).ToHashSet();
                _mods = visible.Concat(_mods.Where(mod => !moved.Contains(mod.Manifest.Guid))).ToList();
                await SaveCurrentAsync();
            }
            else
            {
                group.ModGuids.Clear();
                foreach (var mod in visible)
                    group.ModGuids.Add(mod.Manifest.Guid);
                await _groupRepository.SaveGroupsAsync(_settings.StorageDirectory, _groups.Groups);
            }
        }
        catch
        {
            _mods = oldOrder.ToList();
            group.ModGuids.Clear();
            foreach (var guid in oldMembers)
                group.ModGuids.Add(guid);
            for (var index = 0; index < separators.Length; index++)
                separators[index].DisplayIndex = oldSeparatorPositions[index];
            try { await _settings.SaveAsync(); }
            catch { }
            throw;
        }
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task MoveToTopAsync(ModData source) => MoveAsync(source, 0);

    public Task MoveToBottomAsync(ModData source)
    {
        var count = SelectableMods().Count();
        return MoveAsync(source, count);
    }

    public Task MoveToPositionAsync(ModData source, int oneBasedPosition)
    {
        if (oneBasedPosition < 1)
            throw new ArgumentOutOfRangeException(nameof(oneBasedPosition));
        return MoveAsync(source, oneBasedPosition - 1);
    }

    public async Task RenameSeparatorAsync(ModSeparator separator, string name)
    {
        GuardSeparator(separator);
        var value = name.Trim();
        if (value.Length == 0 || value.Length > 64)
            throw new ArgumentException("Separator name must contain 1 to 64 characters.", nameof(name));
        var previous = separator.Name;
        separator.Name = value;
        try { await _settings.SaveAsync(); }
        catch
        {
            separator.Name = previous;
            throw;
        }
        RebuildRows();
    }

    public async Task SetSeparatorColorAsync(ModSeparator separator, string color)
    {
        GuardSeparator(separator);
        if (!IsColorCode(color))
            throw new ArgumentException("Separator color must be #AARRGGBB.", nameof(color));
        var previous = separator.Color;
        separator.Color = color;
        try { await _settings.SaveAsync(); }
        catch
        {
            separator.Color = previous;
            throw;
        }
        RebuildRows();
    }

    public async Task DeleteSeparatorAsync(ModSeparator separator)
    {
        GuardSeparator(separator);
        var index = _settings.Separators.IndexOf(separator);
        _settings.Separators.RemoveAt(index);
        try { await _settings.SaveAsync(); }
        catch
        {
            _settings.Separators.Insert(index, separator);
            throw;
        }
        RebuildRows();
    }

    public async Task CreateSeparatorAsync(string name)
    {
        if (_settings.IsReadonly || !_settings.ShowSeparator)
            throw new InvalidOperationException("Separators are not writable.");
        var separator = new ModSeparator
        {
            Name = name.Trim(),
            Color = "#FF6200EE",
            IsExpanded = true,
            DisplayIndex = _rows.Count,
        };
        if (separator.Name.Length == 0)
            throw new ArgumentException("Separator name cannot be empty.", nameof(name));
        _settings.Separators.Add(separator);
        try { await _settings.SaveAsync(); }
        catch
        {
            _settings.Separators.Remove(separator);
            throw;
        }
        RebuildRows();
    }

    private void GuardSeparator(ModSeparator separator)
    {
        if (_settings.IsReadonly || !_settings.ShowSeparator || !_settings.Separators.Contains(separator))
            throw new InvalidOperationException("Separators are not writable.");
    }

    private static bool IsColorCode(string value)
        => value.Length == 9 && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out _);

    public async Task RefreshAsync(CancellationToken cancellationToken = default, bool persistImportedDefaults = false)
    {
        await SaveCurrentAsync();
        var loaded = await _library.LoadAsync(_settings, cancellationToken);
        if (persistImportedDefaults)
            await _library.SaveAsync(_settings, loaded.Mods);
        await _groups.InitAsync(_settings, loaded.Mods);
        _groups.ApplyGroupState(_groups.SelectedGroup.Id, loaded.Mods);
        _mods = loaded.Mods.ToList();
        _problems = loaded.Problems;
        _selected.Clear();
        await WarmSearchCacheAsync(cancellationToken);
        RebuildRows();
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveCurrentAsync() => _groups.SelectedGroup.IsDefault
        ? _library.SaveAsync(_settings, _mods)
        : _groups.SaveSelectedGroupStateAsync(_mods);

    private void SetSelection(Func<ModData, bool> selected)
    {
        foreach (var mod in SelectableMods())
            if (selected(mod))
                _selected.Add(mod.Manifest.Guid);
            else
                _selected.Remove(mod.Manifest.Guid);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IEnumerable<ModData> SelectableMods() => _mode == DashboardCatalogMode.Library
        ? _mods : _groups.FilterMods(_mods);

    private void RebuildRows()
    {
        var visible = SelectableMods().ToList();
        var query = _searchText.Trim();
        if (query.Length > 0)
        {
            if (query.StartsWith('@'))
            {
                var name = query[1..];
                if (name.Length > 0)
                {
                    var matchingTags = _settings.Tags.Where(tag =>
                        tag.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                        .Select(tag => tag.Id).ToHashSet();
                    visible = visible.Where(mod => mod.TagIds.Any(matchingTags.Contains)).ToList();
                }
            }
            else if (_settings.EnableFuzzySearch)
            {
                visible = visible.Where(mod =>
                {
                    var cache = _pinyin.GetValueOrDefault(mod.Manifest.Guid);
                    return FuzzySearchMatcher.IsMatch(mod.Manifest.Name, query,
                        _settings.CaseSensitiveSearch, cache.Full, cache.Initials);
                }).ToList();
            }
            else
            {
                var comparison = _settings.CaseSensitiveSearch
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                visible = visible.Where(mod => mod.Manifest.Name.Contains(query, comparison)).ToList();
            }
            _rows = visible.Cast<object>().ToArray();
        }
        else
        {
            var rows = new List<object>(visible.Count + _settings.Separators.Count);
            rows.AddRange(visible);
            if (_settings.ShowSeparator)
                foreach (var separator in _settings.Separators.OrderBy(separator =>
                             separator.DisplayIndex >= 0 ? separator.DisplayIndex : int.MaxValue))
                    rows.Insert(separator.DisplayIndex >= 0
                        ? Math.Min(separator.DisplayIndex, rows.Count) : rows.Count, separator);
            _rows = rows;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WarmSearchCacheAsync(CancellationToken cancellationToken)
    {
        var names = _mods.Select(mod => (mod.Manifest.Guid, mod.Manifest.Name)).ToArray();
        Dictionary<Guid, (string Full, string Initials)> cache;
        try
        {
            cache = await Task.Run(() =>
            {
                var result = new Dictionary<Guid, (string Full, string Initials)>(names.Length);
                foreach (var (guid, name) in names)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result[guid] = (WordsHelper.GetPinyin(name, false).ToLowerInvariant(),
                        WordsHelper.GetFirstPinyin(name).ToLowerInvariant());
                }
                return result;
            }, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            cache = [];
        }
        _pinyin.Clear();
        foreach (var (guid, value) in cache)
            _pinyin[guid] = value;
    }
}
