using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private void OpenCreateMod()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        var editor = new CreateModEditor(settings, localization);
        _createPage = new CreatePageView(editor, localization, _layout.Window,
            () => CreateModAsync(editor), async () =>
            {
                ShowDashboard();
                StartAutoTag();
                await Task.CompletedTask;
            }, ShowDashboard, FormatImportProblem, ReportError);
        _layout.PagePresenter.Content = _createPage;
    }

    private async Task<ModProblem[]> CreateModAsync(CreateModEditor editor)
    {
        if (_tasks is null)
            throw new InvalidOperationException("Task service is not ready.");
        await CancelAutoTagAsync();
        await workspace.SaveCurrentAsync();
        var service = CreateModService();
        await Task.Run(() => service.Init(settings));
        await service.HashMigrationTask;
        ModData? added = null;
        void TrackAdded(ModData mod) => added = mod;
        service.ModAdded += TrackAdded;
        ModProblem[] problems;
        try
        {
            problems = await _tasks.RunAsync(
                localization["CreatePage.Title"], localization["SettingsPage.PleaseWait"],
                (_, _) => editor.CreateAsync(service), isForeground: true);
        }
        finally
        {
            service.ModAdded -= TrackAdded;
        }

        if (problems.Length == 0 && added is not null)
        {
            var defaultGroup = workspace.Groups.Groups.First(group => group.IsDefault);
            await workspace.AddToGroupAsync(defaultGroup.Id, [added]);
            workspace.SetSearchText(string.Empty);
            await workspace.RefreshAsync(persistImportedDefaults: true);
        }
        return problems;
    }
}
