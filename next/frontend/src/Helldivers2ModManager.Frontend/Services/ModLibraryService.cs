using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ModGroupInfo(Guid Id, string Name);

public sealed record ModLibraryLoadResult(
    IReadOnlyList<ModItem> Mods,
    IReadOnlyList<ModGroupInfo> Groups,
    IReadOnlyList<string> Problems);

/// <summary>模组库完整状态快照：平面启用状态 + 全部分组定义与成员，用于二分排查的还原。</summary>
public sealed record LibrarySnapshot(
    IReadOnlyList<EnabledStateRecord> FlatStates,
    IReadOnlyList<ProfileGroupRecord> Groups,
    IReadOnlyDictionary<Guid, IReadOnlyList<EnabledStateRecord>> GroupMembers);

public sealed record ImportArchiveOutcome(
    BackgroundTaskResult TaskResult,
    IReadOnlyList<string> Problems,
    int ImportedCount);

public sealed class ModLibraryService(
    ModDirectoryService directoryService,
    ModArchiveService archiveService,
    EnabledStateRepository enabledStates,
    ProfileRepository profiles,
    GroupRepository groups,
    ApplicationSettingsService settings,
    LocalizationCatalog localization,
    TaskExecutionService taskRunner)
{
    public async Task<ModLibraryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        var storage = new DirectoryInfo(settings.Current.StorageDirectory);
        var discovery = directoryService.DiscoverMods(storage);
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var states = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(record => record.ModGuid);
        var appSettings = settings.Current;
        var tagsByGuid = appSettings.Tags.ToDictionary(tag => tag.Id);
        var items = discovery.Mods.Select(discovered =>
        {
            var item = new ModItem(discovered);
            if (states.TryGetValue(item.Id, out var state))
            {
                item.IsEnabled = state.Enabled;
                var runtime = ProfileStateService.DeserializeRuntimeState(state.StateJson);
                item.TagIds = [.. runtime.TagIds ?? []];
                item.EnabledOptions = [.. runtime.EnabledOptions];
                item.SelectedOptions = [.. runtime.SelectedOptions];
                item.SortOrder = state.SortOrder;
            }

            item.TagIds.RemoveAll(tag => !tagsByGuid.ContainsKey(tag));
            return item;
        }).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var groupRecords = await groups.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        foreach (var group in groupRecords)
        {
            var memberIds = (await groups.LoadMemberIdsAsync(profileId, group.Id, cancellationToken).ConfigureAwait(false))
                .ToHashSet();
            foreach (var item in items)
            {
                if (memberIds.Contains(item.Id))
                {
                    item.SetGroup(group.Id, group.Name);
                }
            }
        }

        var infos = groupRecords
            .OrderBy(group => group.DisplayIndex)
            .Select(group => new ModGroupInfo(group.Id, group.Name))
            .ToArray();
        var problems = discovery.Problems.Select(error => error.Message).ToList();
        var missingIds = states.Keys.Except(items.Select(item => item.Id)).ToArray();
        if (missingIds.Length > 0)
        {
            await enabledStates.DeleteByGuidsAsync(missingIds, cancellationToken).ConfigureAwait(false);
        }

        return new(items, infos, problems);
    }

    public async Task SaveAsync(IReadOnlyList<ModItem> mods, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var ordered = mods.Select((item, index) => (item, index)).ToArray();
        var flat = ordered
            .Where(pair => pair.item.GroupId is null)
            .Select(pair => CreateRecord(pair.item, pair.index));
        await enabledStates.ReplaceAllAsync(flat, cancellationToken).ConfigureAwait(false);

        foreach (var group in await groups.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            var members = ordered
                .Where(pair => pair.item.GroupId == group.Id)
                .Select(pair => CreateRecord(pair.item, pair.index))
                .ToArray();
            await groups.ReplaceMembersAsync(profileId, group.Id, members, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveItemAsync(ModItem item, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        if (item.GroupId is { } groupId)
        {
            var members = (await groups.LoadMembersAsync(profileId, groupId, cancellationToken).ConfigureAwait(false)).ToList();
            var index = members.FindIndex(record => record.ModGuid == item.Id);
            var record = new EnabledStateRecord(
                item.Id,
                item.IsEnabled,
                index >= 0 ? members[index].SortOrder : item.SortOrder,
                ProfileStateService.SerializeRuntimeState(item.CreateRuntimeState()));
            if (index >= 0)
            {
                members[index] = record;
            }
            else
            {
                members.Add(record);
            }

            await groups.ReplaceMembersAsync(profileId, groupId, members, cancellationToken).ConfigureAwait(false);
            return;
        }

        var records = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var existingIndex = records.FindIndex(record => record.ModGuid == item.Id);
        var flatRecord = new EnabledStateRecord(
            item.Id,
            item.IsEnabled,
            existingIndex >= 0 ? records[existingIndex].SortOrder : records.Count,
            ProfileStateService.SerializeRuntimeState(item.CreateRuntimeState()));
        if (existingIndex >= 0)
        {
            records[existingIndex] = flatRecord;
        }
        else
        {
            records.Add(flatRecord);
        }

        await enabledStates.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModGroupInfo> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var existing = await groups.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ValidateGroupName(name, existing);
        var group = new ProfileGroupRecord(Guid.NewGuid(), name.Trim(), existing.Count, DateTimeOffset.UtcNow);
        await groups.SaveAsync(profileId, group, cancellationToken).ConfigureAwait(false);
        return new(group.Id, group.Name);
    }

    public async Task RenameGroupAsync(Guid groupId, string name, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var existing = await groups.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        ValidateGroupName(name, existing.Where(group => group.Id != groupId));
        var record = existing.First(group => group.Id == groupId);
        await groups.SaveAsync(profileId, record with { Name = name.Trim() }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除分组：成员的启用状态并入平面状态，避免随分组一起被清除。</summary>
    public async Task DeleteGroupAsync(Guid groupId, IReadOnlyList<ModItem> mods, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var members = await groups.LoadMembersAsync(profileId, groupId, cancellationToken).ConfigureAwait(false);
        var flat = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var flatIds = flat.Select(record => record.ModGuid).ToHashSet();
        flat.AddRange(members.Where(record => !flatIds.Contains(record.ModGuid)));
        await enabledStates.ReplaceAllAsync(flat, cancellationToken).ConfigureAwait(false);

        foreach (var item in mods.Where(mod => mod.GroupId == groupId).ToArray())
        {
            item.SetGroup(null, null);
        }

        await SaveAsync(mods, cancellationToken).ConfigureAwait(false);
        await groups.DeleteAsync(profileId, groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把指定模组移入目标分组；<c>null</c> 表示移出分组。状态随下一次 SaveAsync 全量落库。</summary>
    public async Task SetModsGroupAsync(
        IReadOnlyList<ModItem> mods,
        IEnumerable<ModItem> targets,
        Guid? targetGroupId,
        string? targetGroupName,
        CancellationToken cancellationToken = default)
    {
        var targetIds = targets.Select(mod => mod.Id).ToHashSet();
        foreach (var item in mods.Where(mod => targetIds.Contains(mod.Id) && mod.GroupId != targetGroupId).ToArray())
        {
            item.SetGroup(targetGroupId, targetGroupName);
        }

        await SaveAsync(mods, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibrarySnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        var groupRecords = await groups.LoadForProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        var members = new Dictionary<Guid, IReadOnlyList<EnabledStateRecord>>();
        foreach (var group in groupRecords)
        {
            members[group.Id] = await groups.LoadMembersAsync(profileId, group.Id, cancellationToken).ConfigureAwait(false);
        }

        return new(
            await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false),
            groupRecords,
            members);
    }

    public async Task RestoreSnapshotAsync(LibrarySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
        await enabledStates.ReplaceAllAsync(snapshot.FlatStates, cancellationToken).ConfigureAwait(false);
        foreach (var group in snapshot.Groups)
        {
            await groups.SaveAsync(profileId, group, cancellationToken).ConfigureAwait(false);
            if (snapshot.GroupMembers.TryGetValue(group.Id, out var members))
            {
                await groups.ReplaceMembersAsync(profileId, group.Id, members, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<ImportArchiveOutcome> ImportAsync(
        IReadOnlyList<string> archivePaths,
        Action<IBackgroundTaskContext, int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        var storage = new DirectoryInfo(settings.Current.StorageDirectory);
        var temp = new DirectoryInfo(settings.Current.TempDirectory);
        var problems = new List<string>();
        var imported = 0;
        var result = await taskRunner.RunAsync(
            localization.GetString("Next.Tasks.Import"),
            string.Format(localization.GetString("Next.Tasks.ImportingFormat"), archivePaths.Count),
            async (task, token) =>
            {
                for (var index = 0; index < archivePaths.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var archivePath = archivePaths[index];
                    progress?.Invoke(task, index, archivePaths.Count, Path.GetFileName(archivePath));
                    var archiveResult = await archiveService.ImportArchiveAsync(
                        new FileInfo(archivePath),
                        storage,
                        temp,
                        settings.Current.DeleteToRecycleBin,
                        cancellationToken: token).ConfigureAwait(false);
                    imported += archiveResult.ImportedMods.Count;
                    problems.AddRange(archiveResult.Problems.Select(problem => $"{problem.ArchivePath}: {problem.Detail}"));
                }

                progress?.Invoke(task, archivePaths.Count, archivePaths.Count, string.Empty);
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result, problems, imported);
    }

    public async Task<Result> DeleteAsync(ModItem item, CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        var storage = new DirectoryInfo(settings.Current.StorageDirectory);
        var result = await directoryService.DeleteAsync(
            item.Directory,
            storage,
            item.Id,
            settings.Current.DeleteToRecycleBin,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            await enabledStates.DeleteByGuidsAsync([item.Id], cancellationToken).ConfigureAwait(false);
            if (item.GroupId is { } groupId)
            {
                var profileId = (await profiles.GetOrCreateDefaultAsync(cancellationToken).ConfigureAwait(false)).Id;
                var members = (await groups.LoadMembersAsync(profileId, groupId, cancellationToken).ConfigureAwait(false))
                    .Where(record => record.ModGuid != item.Id)
                    .ToArray();
                await groups.ReplaceMembersAsync(profileId, groupId, members, cancellationToken).ConfigureAwait(false);
            }
        }

        return result;
    }

    public async Task ExportAsync(
        ModItem item,
        string outputPath,
        ArchiveExportFormat format,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await archiveService.ExportAsync(item.Directory, outputPath, format, progress, cancellationToken).ConfigureAwait(false);
    }

    private static EnabledStateRecord CreateRecord(ModItem item, int index) => new(
        item.Id,
        item.IsEnabled,
        index,
        ProfileStateService.SerializeRuntimeState(item.CreateRuntimeState()));

    private static void ValidateGroupName(string name, IEnumerable<ProfileGroupRecord> existing)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Group name must not be empty.");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 40)
        {
            throw new InvalidOperationException("Group name is too long (max 40 characters).");
        }

        if (existing.Any(group => string.Equals(group.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException($"A group named '{trimmed}' already exists.");
        }
    }

    private async Task EnsureStorageAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.Current.StorageDirectory);
        Directory.CreateDirectory(Path.Combine(settings.Current.StorageDirectory, "Mods"));
        Directory.CreateDirectory(settings.Current.TempDirectory);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
