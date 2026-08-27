using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class LibraryDeploymentService(
    ModLibraryService library,
    DeploymentServiceFacade deployment)
{
    public async Task<BackgroundTaskResult> DeployChangedItemAsync(
        IReadOnlyList<ModItem> mods,
        ModItem changedMod,
        IReadOnlyList<DeployOptionItem> options,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ApplyOptions(changedMod, options);
        await library.SaveAsync(mods, cancellationToken).ConfigureAwait(false);
        return await deployment.DeployAsync([changedMod], progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundTaskResult> DeployEnabledModsAsync(
        IReadOnlyList<ModItem> mods,
        IProgress<DeploymentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await library.SaveAsync(mods, cancellationToken).ConfigureAwait(false);
        var enabled = mods.Where(mod => mod.IsEnabled).OrderBy(mod => mod.SortOrder).ToArray();
        if (enabled.Length == 0)
        {
            await deployment.PurgeAsync(cancellationToken).ConfigureAwait(false);
            return new BackgroundTaskResult(Guid.NewGuid(), BackgroundTaskStatus.Succeeded, null);
        }

        return await deployment.DeployAsync(enabled, progress, cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<DeployOptionItem> CreateOptions(ModItem mod) => mod.Source.Manifest switch
    {
        V1ModManifest manifest => [.. (manifest.Options ?? []).Select((option, index) => new DeployOptionItem
        {
            Index = index,
            Name = option.Name,
            Description = option.Description,
            SubOptions = [.. (option.SubOptions ?? []).Select(sub => sub.Name)],
            IsEnabled = index < mod.EnabledOptions.Count ? mod.EnabledOptions[index] : true,
            SelectedSubOption = Math.Clamp(
                index < mod.SelectedOptions.Count ? mod.SelectedOptions[index] : 0,
                0,
                Math.Max(0, (option.SubOptions?.Count ?? 1) - 1)),
        })],
        LegacyModManifest manifest => [.. (manifest.Options ?? []).Select((name, index) => new DeployOptionItem
        {
            Index = index,
            Name = name,
            SubOptions = [],
            IsEnabled = mod.SelectedOptions.FirstOrDefault() == index,
            SelectedSubOption = 0,
        })],
        _ => [],
    };

    public static void ApplyOptions(ModItem mod, IReadOnlyList<DeployOptionItem> options)
    {
        if (mod.Source.Manifest is V1ModManifest)
        {
            mod.EnabledOptions = [.. options.Select(option => option.IsEnabled)];
            mod.SelectedOptions =
            [
                .. options.Select(option => Math.Clamp(
                    option.SelectedSubOption,
                    0,
                    Math.Max(0, option.SubOptions.Count - 1))),
            ];
        }
        else
        {
            var selected = -1;
            for (var index = 0; index < options.Count; index++)
            {
                if (options[index].IsEnabled)
                {
                    selected = index;
                    break;
                }
            }

            mod.SelectedOptions = [Math.Max(0, selected)];
        }
    }
}
