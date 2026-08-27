using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class AutoTaggingFacade(
    ModTypeDetectionService detector,
    AutoTaggingService autoTagger,
    ApplicationSettingsService settings,
    EnabledStateRepository enabledStates,
    ModLibraryService library,
    LocalizationCatalog localization,
    TaskExecutionService tasks)
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _isRunning;

    public async Task RunAsync(IReadOnlyList<ModItem> mods, CancellationToken cancellationToken = default)
    {
        if (!settings.Current.EnableAutoTagging || mods.Count == 0)
        {
            return;
        }

        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            await RunCoreAsync(mods, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isRunning = false;
            _runLock.Release();
        }
    }

    private async Task RunCoreAsync(IReadOnlyList<ModItem> mods, CancellationToken cancellationToken)
    {
        var tagNames = ModTypeDetectionService.BuiltInTags
            .ToDictionary(definition => definition.Type, definition => localization.GetString(definition.NameKey));
        await tasks.RunAsync(
            "自动打标签",
            $"正在分析 {mods.Count} 个模组",
            async (task, token) =>
            {
                var scanStep = task.AddStep("扫描模组类型", BackgroundStepStatus.Running);
                var directories = mods.Select(mod => mod.Directory).ToArray();
                var detections = await detector.DetectAllAsync(directories, token).ConfigureAwait(false);
                task.UpdateStep(scanStep, "扫描模组类型", BackgroundStepStatus.Succeeded);

                var applyStep = task.AddStep("应用标签映射", BackgroundStepStatus.Running);
                var requests = mods.Select(mod => new AutoTagRequest(
                    mod.Directory.FullName,
                    mod.TagIds,
                    detections.GetValueOrDefault(mod.Directory.FullName)?.Types ?? [ModType.Unknown])).ToArray();
                var result = autoTagger.Apply(
                    requests,
                    settings.Current.Tags,
                    settings.Current.AutoTagMappings,
                    type => tagNames.GetValueOrDefault(type, type.ToString()),
                    settings.Current.AutoTagCreateMissingTags);
                settings.Current.Tags = new List<TagSetting>(result.Tags);
                await settings.SaveAsync(settings.Current, token).ConfigureAwait(false);

                foreach (var mod in mods)
                {
                    mod.TagIds = [.. result.TagIdsByPath[mod.Directory.FullName]];
                }

                await library.SaveAsync(mods, token).ConfigureAwait(false);
                var saved = await enabledStates.LoadAllAsync(token).ConfigureAwait(false);
                var byGuid = mods.ToDictionary(mod => mod.Id);
                var records = saved.Select(record => byGuid.TryGetValue(record.ModGuid, out var mod)
                    ? record with
                    {
                        StateJson = ProfileStateService.SerializeRuntimeState(mod.CreateRuntimeState()),
                    }
                    : record).ToArray();
                await enabledStates.ReplaceAllAsync(records, token).ConfigureAwait(false);
                task.UpdateStep(applyStep, "应用标签映射", BackgroundStepStatus.Succeeded);
                task.ReportProgress(1);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
