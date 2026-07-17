using System.IO;

namespace Helldivers2ModManager.Models;

/// <summary>
/// 保存与部署共用的不可变主页状态快照。
/// </summary>
internal sealed class ProfileSnapshot
{
    public long Sequence { get; }

    public Guid GroupId { get; }

    public bool IsDefaultGroup { get; }

    public IReadOnlyList<ProfileModSnapshot> Mods { get; }

    public IReadOnlyList<Guid> Order { get; }

    private ProfileSnapshot(long sequence, Guid groupId, bool isDefaultGroup, ProfileModSnapshot[] mods)
    {
        Sequence = sequence;
        GroupId = groupId;
        IsDefaultGroup = isDefaultGroup;
        Mods = Array.AsReadOnly(mods);
        Order = Array.AsReadOnly(mods.Select(static mod => mod.Guid).ToArray());
    }

    public static ProfileSnapshot Capture(
        long sequence,
        Guid groupId,
        bool isDefaultGroup,
        IEnumerable<ModData> mods,
        IEnumerable<Guid>? preferredOrder = null)
    {
        var sourceMods = mods.ToList();
        var modsByGuid = sourceMods.ToDictionary(static mod => mod.Manifest.Guid);
        var snapshots = new List<ProfileModSnapshot>(sourceMods.Count);

        if (preferredOrder is not null)
        {
            foreach (var guid in preferredOrder)
            {
                if (modsByGuid.Remove(guid, out var mod))
                    snapshots.Add(ProfileModSnapshot.Capture(mod));
            }
        }

        foreach (var mod in sourceMods)
        {
            if (modsByGuid.Remove(mod.Manifest.Guid))
                snapshots.Add(ProfileModSnapshot.Capture(mod));
        }

        return new ProfileSnapshot(sequence, groupId, isDefaultGroup, [.. snapshots]);
    }

    public IReadOnlyList<EnabledData> ToEnabledData()
    {
        return Mods.Select(static mod => mod.ToEnabledData()).ToArray();
    }

    public IReadOnlyList<GroupedEnabledData> ToGroupedEnabledData()
    {
        return Mods.Select((mod, index) => mod.ToGroupedEnabledData(GroupId, index)).ToArray();
    }
}

internal sealed class ProfileModSnapshot
{
    private readonly DirectoryInfo _directory;
    private readonly IModManifest _manifest;
    private readonly bool[] _toggled;
    private readonly int[] _selected;
    private readonly Guid[] _tagIds;

    public Guid Guid => _manifest.Guid;

    public bool Enabled { get; }

    private ProfileModSnapshot(
        DirectoryInfo directory,
        IModManifest manifest,
        bool enabled,
        bool[] toggled,
        int[] selected,
        Guid[] tagIds)
    {
        _directory = directory;
        _manifest = manifest;
        Enabled = enabled;
        _toggled = toggled;
        _selected = selected;
        _tagIds = tagIds;
    }

    public static ProfileModSnapshot Capture(ModData mod)
    {
        return new ProfileModSnapshot(
            new DirectoryInfo(mod.Directory.FullName),
            mod.Manifest,
            mod.Enabled,
            [.. mod.EnabledOptions],
            [.. mod.SelectedOptions],
            [.. mod.TagIds]);
    }

    public EnabledData ToEnabledData()
    {
        return new EnabledData
        {
            Guid = Guid,
            Enabled = Enabled,
            Toggled = [.. _toggled],
            Selected = [.. _selected],
            TagIds = [.. _tagIds],
        };
    }

    public GroupedEnabledData ToGroupedEnabledData(Guid groupId, int sortOrder)
    {
        return new GroupedEnabledData
        {
            GroupId = groupId,
            Guid = Guid,
            Enabled = Enabled,
            Toggled = [.. _toggled],
            Selected = [.. _selected],
            SortOrder = sortOrder,
        };
    }

    public ModData CreateDeploymentMod()
    {
        var result = new ModData(_directory, _manifest);
        var enabledData = ToEnabledData();
        result.ApplyData(enabledData);
        return result;
    }
}
