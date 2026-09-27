using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private Task DeployModsAsync() => RunModOperationAsync(deploy: true);
    private Task PurgeModsAsync() => RunModOperationAsync(deploy: false);

    private async Task RunModOperationAsync(bool deploy)
    {
        if (_modOperationActive || _batchRepairActive || _tasks is null || _layout is null || _dispatcher is null)
            return;
        if (!settings.Initialized || string.IsNullOrWhiteSpace(settings.GameDirectory)
            || !Directory.Exists(Path.Combine(settings.GameDirectory, "data")))
            throw new InvalidOperationException(localization[deploy
                ? "DashboardPage.DeployNoGameDir" : "DashboardPage.PurgeNoGameDir"]);

        var snapshot = deploy ? workspace.CaptureProfileSnapshot() : null;
        var mods = snapshot is null ? [] : DeploymentOrderHelper.BuildDeploymentMods(snapshot,
            settings.UseDeploymentOrder, settings.DeploymentOrderGuids, settings.DeployBottomToTop);
        var title = localization[deploy ? "DashboardPage.DeployProgress" : "DashboardPage.PurgeProgress"];
        var messageBox = _messageBoxOverlay;
        if (messageBox is null)
            return;
        BackgroundTaskItem? task = null;
        var completed = 0;
        _modOperationActive = true;
        try
        {
            messageBox.ShowProgress(title, localization["SettingsPage.PleaseWait"]);
            if (deploy)
                await workspace.SaveCurrentAsync();
            var service = CreateModService();
            await Task.Run(() => service.Init(settings));
            await _tasks.RunAsync(
                localization[deploy ? "DashboardPage.DeployMods" : "BackgroundTasksPage.TaskTypePurge"],
                localization["SettingsPage.PleaseWait"],
                async (context, cancellationToken) =>
                {
                    if (!deploy)
                    {
                        await service.PurgeAsync();
                        return;
                    }

                    await service.DeployAsync(mods,
                        step =>
                        {
                            context.ReportStep(step);
                            _ = _dispatcher.BeginInvoke(() => messageBox.ReportProgressStep(step));
                        },
                        detail =>
                        {
                            context.ReportStepDetail(detail);
                            _ = _dispatcher.BeginInvoke(() => messageBox.UpdateProgress(detail, null));
                        },
                        () =>
                        {
                            context.CompleteStep();
                            _ = _dispatcher.BeginInvoke(() =>
                            {
                                messageBox.CompleteProgressStep();
                                completed++;
                                messageBox.UpdateProgress(localization["SettingsPage.PleaseWait"],
                                    mods.Length == 0 ? 1 : Math.Min(1, (double)completed / mods.Length));
                            });
                        },
                        () =>
                        {
                            context.FailStep();
                            _ = _dispatcher.BeginInvoke(messageBox.FailProgressStep);
                        });
                },
                localization[deploy ? "DashboardPage.DeploySuccess" : "BackgroundTasksPage.PurgeComplete"],
                taskCreated: created => task = created,
                isForeground: true);
            await DrainUiQueueAsync();
            messageBox.CloseProgress();
            ShowModOperationResult(title,
                localization[deploy ? "DashboardPage.DeploySuccess" : "BackgroundTasksPage.PurgeComplete"],
                task?.Steps);
        }
        catch (Exception ex)
        {
            await DrainUiQueueAsync();
            messageBox.CloseProgress();
            ShowModOperationResult(title, ex.Message, task?.Steps, error: true);
        }
        finally
        {
            _modOperationActive = false;
        }
    }

    private Task DrainUiQueueAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher!.BeginInvoke(() => completion.SetResult());
        return completion.Task;
    }

    private void ShowModOperationResult(string title, string message, IEnumerable<TaskStepItem>? steps,
        bool error = false)
    {
        var rows = new List<string> { message };
        foreach (var step in steps ?? [])
            rows.Add((step.Status == TaskStepStatus.Completed ? "\u2713 "
                    : step.Status == TaskStepStatus.Failed ? "\u2717 " : "")
                    + step.Text + (step.Detail is null ? "" : "\n" + step.Detail));
        var summary = string.Join(Environment.NewLine + Environment.NewLine, rows);
        if (error) _messageBoxOverlay!.ShowError(title, summary);
        else _messageBoxOverlay!.ShowInfo(title, summary);
    }
}
