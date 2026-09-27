using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private void ShowImagePreview(ImageSource source) => _imagePreviewOverlay?.Show(source);

    private void OpenEditMod(ModData? mod)
    {
        if (_layout is null || mod is null)
            return;
        _editPage = new EditPageView(workspace, mod, _translation, localization, ShowDashboard, ReportError, ShowImagePreview);
        _layout.PagePresenter.Content = _editPage;
    }

    private void OpenManifestEdit(ModData? mod)
    {
        if (_layout is null || mod is null)
            return;
        _manifestEditor = new ManifestEditor(mod, localization);
        _manifestPage = new ManifestEditPageView(_manifestEditor, localization, _layout.Window,
            async () =>
            {
                await workspace.RefreshAsync();
                ShowDashboard();
            }, ReportError, ShowImagePreview);
        _layout.PagePresenter.Content = _manifestPage;
    }

    private async Task DeleteModAsync(ModData? mod)
    {
        if (_layout is null)
            return;
        if (mod is null)
        {
            var selected = workspace.SelectedMods;
            if (selected.Count == 0)
                return;
            if (workspace.Mode == DashboardCatalogMode.Workspace)
            {
                await workspace.RemoveSelectedFromCurrentGroupAsync();
                return;
            }
            await DeleteSelectedModsFromLibraryAsync(selected);
            return;
        }

        var message = localization["DashboardPage.DeleteConfirmPrefix"] + mod.Manifest.Name
            + localization["DashboardPage.DeleteConfirmSuffix"]
            + (settings.DeleteToRecycleBin ? localization["DashboardPage.RecycleBinConfirm"]
                : localization["DashboardPage.PermanentDeleteConfirm"]);
        if (_messageBoxOverlay is null ||
            !await _messageBoxOverlay.ConfirmAsync(localization["DashboardPage.DeleteConfirmTitle"], message))
            return;

        try
        {
            await CancelAutoTagAsync();
            await workspace.SaveCurrentAsync();
            var service = CreateModService();
            await Task.Run(() => service.Init(settings));
            await service.HashMigrationTask;
            if (_tasks is not null)
                await _tasks.RunAsync(localization["DashboardPage.DeleteModProgress"], mod.Manifest.Name,
                    async (_, _) => await service.RemoveAsync(mod), isForeground: true);
            else
                await service.RemoveAsync(mod);
            await CleanupDeletedModsAsync([mod]);
            await workspace.RefreshAsync(persistImportedDefaults: true);
        }
        catch (Exception ex) { ReportError(ex); }
        finally { StartAutoTag(); }
    }

    private async Task DeleteSelectedModsFromLibraryAsync(IReadOnlyList<ModData> selected)
    {
        var deleteMessage = settings.DeleteToRecycleBin
            ? localization["DashboardPage.RecycleBinConfirm"]
            : localization["DashboardPage.PermanentDeleteConfirm"];
        var message = localization["DashboardPage.BatchDeleteConfirm"]
            .Replace("{count}", selected.Count.ToString()) + deleteMessage;
        if (_messageBoxOverlay is null || !await _messageBoxOverlay.ConfirmAsync(
                localization["DashboardPage.BatchDeleteTitle"], message))
            return;

        await CancelAutoTagAsync();
        await workspace.SaveCurrentAsync();
        var service = CreateModService();
        var removed = new List<ModData>(selected.Count);
        try
        {
            await Task.Run(() => service.Init(settings));
            await service.HashMigrationTask;
            if (_tasks is null)
            {
                foreach (var item in selected)
                {
                    await service.RemoveAsync(item);
                    removed.Add(item);
                }
            }
            else
            {
                await _tasks.RunAsync(
                    localization["DashboardPage.BatchDeleteProgress"],
                    selected.Count.ToString(),
                    async (context, cancellationToken) =>
                    {
                        for (var index = 0; index < selected.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var item = selected[index];
                            await service.RemoveAsync(item);
                            removed.Add(item);
                            context.Report(item.Manifest.Name, (double)(index + 1) / selected.Count, false);
                        }
                    },
                    isForeground: true);
            }
        }
        catch (Exception ex)
        {
            ReportError(new InvalidOperationException(
                localization["DashboardPage.BatchDeleteFailed"] + ex.Message, ex));
        }
        finally
        {
            try
            {
                await CleanupDeletedModsAsync(removed);
            }
            catch (Exception ex) { ReportError(ex); }
            await workspace.RefreshAsync(persistImportedDefaults: true);
            StartAutoTag();
        }
    }

    private async Task CleanupDeletedModsAsync(IReadOnlyCollection<ModData> removed)
    {
        if (removed.Count == 0 || settings.IsReadonly)
            return;
        var guids = removed.Select(static item => item.Manifest.Guid).ToArray();
        await new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database)
            .DeleteByGuidsAsync(settings.StorageDirectory, guids);
        await new ModLinkRepository(NullLogger<ModLinkRepository>.Instance, database)
            .DeleteByGuidsAsync(settings.StorageDirectory, guids);
        await workspace.Groups.RemoveModsFromAllGroupsAsync(guids);
        var versions = _versionRepository ?? new VersionCheckRepository(
            NullLogger<VersionCheckRepository>.Instance, database);
        foreach (var guid in guids)
            await versions.DeleteByGuidAsync(settings.StorageDirectory, guid);
    }

}
