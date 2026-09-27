using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private Task? _hashRecomputeTask;

    internal async Task<SettingsService> LoadPersistedSettingsAsync()
    {
        var path = settingsFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helldivers2ModManagerJalium", "settings.json");
        var saved = new SettingsService(NullLogger<SettingsService>.Instance,
            path, Path.GetDirectoryName(path)!);
        if (!await saved.InitAsync())
            throw new FileNotFoundException("Saved settings are unavailable.", path);
        return saved;
    }

    private async Task RecomputeHashesAsync()
    {
        if (workspace.Mods.Count == 0)
        {
            ShowBisectNotice("SettingsPage.RecomputeHashes", "SettingsPage.NoModsForHash");
            return;
        }
        if (_hashRecomputeTask is { IsCompleted: false })
            return;
        var message = localization["SettingsPage.RecomputeHashMsg"]
            .Replace("{count}", workspace.Mods.Count.ToString());
        if (!await ConfirmBisect("Common.WarningPrefix", message))
            return;
        var saved = await LoadPersistedSettingsAsync();
        var repository = new FileHashRepository(NullLogger<FileHashRepository>.Instance, database);
        var service = new ModHashService(NullLogger<ModHashService>.Instance,
            repository, database, localization, _tasks!);
        service.Init(saved);
        var mods = workspace.Mods.ToArray();
        _hashRecomputeTask = ObserveRecomputeAsync(service.ForceRecomputeAllAsync(mods));
        ShowBisectNotice("SettingsPage.RecomputeHashes", "SettingsPage.RecomputeHashStarted");
    }

    private async Task ObserveRecomputeAsync(Task recompute)
    {
        try { await recompute; }
        catch (Exception ex)
        {
            if (_dispatcher is not null)
                _ = _dispatcher.BeginInvoke(() => ReportError(ex));
        }
    }

    private async Task HardPurgeAsync()
    {
        if (new GameProcessService(NullLogger<GameProcessService>.Instance).IsGameRunning())
            throw new InvalidOperationException(localization["ModService.GameRunningBlocked"]);
        var saved = await LoadPersistedSettingsAsync();
        await _tasks!.RunAsync(localization["SettingsPage.ForceCleanPatches"],
            localization["SettingsPage.ForceCleanPatchesDesc"],
            async (context, token) =>
            {
                await SettingsToolsWorkflow.HardPurgeAsync(saved.StorageDirectory,
                    saved.GameDirectory, value => context.Report(progress: value), token);
            });
    }

    private async Task ResetSettingsAsync()
    {
        if (await ConfirmBisect("SettingsPage.ResetTitle", localization["SettingsPage.ResetConfirmMsg"]))
        {
            settings.Reset();
            _settingsPage?.Refresh();
        }
    }
}
