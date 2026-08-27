using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record ModLibraryLoadResult(
    IReadOnlyList<ModItem> Mods,
    IReadOnlyList<string> Problems);

public sealed record ImportArchiveOutcome(
    BackgroundTaskResult TaskResult,
    IReadOnlyList<string> Problems,
    int ImportedCount);

public sealed class ModLibraryService(
    ModDirectoryService directoryService,
    ModArchiveService archiveService,
    EnabledStateRepository enabledStates,
    ApplicationSettingsService settings,
    TaskExecutionService taskRunner)
{
    public async Task<ModLibraryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        var storage = new DirectoryInfo(settings.Current.StorageDirectory);
        var discovery = directoryService.DiscoverMods(storage);
        var states = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(record => record.ModGuid);
        var appSettings = settings.Current;
        var tagsByGuid = appSettings.Tags.ToDictionary(tag => tag.Id);
        var items = discovery.Mods.Select((discovered, index) =>
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

        var problems = discovery.Problems.Select(error => error.Message).ToList();
        var missingIds = states.Keys.Except(items.Select(item => item.Id)).ToArray();
        if (missingIds.Length > 0)
        {
            await enabledStates.DeleteByGuidsAsync(missingIds, cancellationToken).ConfigureAwait(false);
        }

        return new(items, problems);
    }

    public async Task SaveAsync(IReadOnlyList<ModItem> mods, CancellationToken cancellationToken = default)
    {
        var records = mods.Select((item, index) => new EnabledStateRecord(
            item.Id,
            item.IsEnabled,
            index,
            ProfileStateService.SerializeRuntimeState(item.CreateRuntimeState())));
        await enabledStates.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveItemAsync(ModItem item, CancellationToken cancellationToken = default)
    {
        var records = (await enabledStates.LoadAllAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var existingIndex = records.FindIndex(record => record.ModGuid == item.Id);
        var record = new EnabledStateRecord(
            item.Id,
            item.IsEnabled,
            existingIndex >= 0 ? records[existingIndex].SortOrder : records.Count,
            ProfileStateService.SerializeRuntimeState(item.CreateRuntimeState()));
        if (existingIndex >= 0)
        {
            records[existingIndex] = record;
        }
        else
        {
            records.Add(record);
        }

        await enabledStates.ReplaceAllAsync(records, cancellationToken).ConfigureAwait(false);
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
            "导入模组",
            $"正在导入 {archivePaths.Count} 个压缩包",
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

    private async Task EnsureStorageAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settings.Current.StorageDirectory);
        Directory.CreateDirectory(Path.Combine(settings.Current.StorageDirectory, "Mods"));
        Directory.CreateDirectory(settings.Current.TempDirectory);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
