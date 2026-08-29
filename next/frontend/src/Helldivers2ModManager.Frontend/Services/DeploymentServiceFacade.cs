using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed record EnabledModSnapshot(IReadOnlyList<ModItem> Mods);

public sealed class DeploymentServiceFacade(
    ModLibraryService library,
    DeploymentService deployment,
    ApplicationSettingsService settings,
    ApplicationPaths paths,
    LocalizationCatalog localization,
    TaskExecutionService taskRunner)
{
    public async Task<IReadOnlyList<ModItem>> LoadEnabledModsAsync(CancellationToken cancellationToken = default)
    {
        var result = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        return [.. result.Mods.Where(item => item.IsEnabled)];
    }

    public async Task<BackgroundTaskResult> DeployAsync(
        IReadOnlyList<ModItem> orderedMods,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var gameData = ResolveGameDataDirectory();
        var inputs = orderedMods.Select(CreateInput).ToArray();
        var options = new DeploymentOptions(
            gameData,
            settings.Current.UseSymbolicLinks,
            settings.Current.SkipList);
        return await taskRunner.RunAsync(
            localization.GetString("Next.Tasks.Deploy"),
            string.Format(localization.GetString("Next.Tasks.DeployingFormat"), inputs.Length),
            (_, token) => deployment.DeployAsync(inputs, options, progress, token),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundTaskResult> PurgeAsync(CancellationToken cancellationToken = default)
    {
        var gameData = ResolveGameDataDirectory();
        return await taskRunner.RunAsync(
            localization.GetString("Next.Tasks.Purge"),
            localization.GetString("Next.Tasks.Purging"),
            (_, token) => deployment.PurgeAsync(gameData, token),
            cancellationToken).ConfigureAwait(false);
    }

    public DeploymentPlan CreatePlan(IReadOnlyList<ModItem> orderedMods)
    {
        var options = new DeploymentOptions(
            ResolveGameDataDirectory(),
            settings.Current.UseSymbolicLinks,
            settings.Current.SkipList);
        return deployment.CreatePlan(orderedMods.Select(CreateInput).ToArray(), options);
    }

    private ModDeploymentInput CreateInput(ModItem item)
        => item.CreateDeploymentInput();

    private DirectoryInfo ResolveGameDataDirectory()
    {
        if (string.IsNullOrWhiteSpace(settings.Current.GameDirectory))
        {
            return new DirectoryInfo(paths.GameData);
        }

        return new DirectoryInfo(Path.Combine(settings.Current.GameDirectory, "data"));
    }
}
