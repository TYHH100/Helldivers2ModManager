using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private ModService? _bisectModService;
    private BisectService? _bisectService;
    private BisectPageView? _bisectPage;

    private async Task OpenBisectAsync()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        await CancelAutoTagAsync();
        await workspace.SaveCurrentAsync();
        if (_bisectService is null)
        {
            _bisectModService = CreateModService();
            var enabledRepository = new EnabledDataRepository(
                NullLogger<EnabledDataRepository>.Instance, database);
            var profile = new ProfileService(NullLogger<ProfileService>.Instance,
                enabledRepository, database,
                new ModLinkRepository(NullLogger<ModLinkRepository>.Instance, database));
            var saves = new ProfileSaveCoordinator(NullLogger<ProfileSaveCoordinator>.Instance,
                profile, workspace.Groups, settings);
            _bisectService = new BisectService(NullLogger<BisectService>.Instance,
                workspace.Groups, _bisectModService, settings, saves, localization);
        }
        _bisectPage ??= new BisectPageView(workspace, _bisectService, localization,
            ShowDashboard, StartBisectAsync, ResumeBisectAsync, AbortBisectAsync, ReportError);
        _bisectPage.Refresh();
        _layout.PagePresenter.Content = _bisectPage;
    }

    private async Task StartBisectAsync()
    {
        var service = _bisectService!;
        if (service.Current is not null)
            return;
        if (!settings.Initialized || string.IsNullOrWhiteSpace(settings.GameDirectory)
            || !Directory.Exists(Path.Combine(settings.GameDirectory, "data")))
        {
            ShowBisectNotice("Bisect.Title", "Bisect.NoGameDir");
            return;
        }
        var enabled = workspace.Groups.FilterMods(workspace.Mods).Count(mod => mod.Enabled);
        if (enabled < 2)
        {
            ShowBisectNotice("Bisect.Title", "Bisect.NeedTwoEnabled");
            return;
        }

        var stale = service.FindStaleTempGroups();
        if (stale.Count > 0)
        {
            var message = localization["Bisect.StaleMessage"]
                .Replace("{names}", string.Join("\n", stale.Select(group => group.Name)));
            if (!await ConfirmBisect("Bisect.StaleTitle", message))
                return;
            if (stale.Any(group => group.Id == workspace.Groups.SelectedGroup.Id))
            {
                var fallback = workspace.Groups.Groups.First(group => group.IsDefault);
                await workspace.Groups.SelectGroupAsync(fallback.Id, workspace.Mods);
            }
            foreach (var group in stale)
                await workspace.Groups.DeleteGroupAsync(group.Id);
            workspace.RefreshTags();
            _bisectPage?.Refresh();
        }

        if (!await ConfirmBisect("Bisect.Start", localization["Bisect.StartConfirmMessage"]
            .Replace("{name}", service.TempGroupName)))
            return;
        try
        {
            if (!_bisectModService!.Initialized)
                await Task.Run(() => _bisectModService.Init(settings));
            await service.StartAsync(workspace.Groups.SelectedGroup, workspace.Mods);
            _bisectPage?.Refresh();
            await RunBisectLoopAsync();
        }
        catch
        {
            await RecoverBisectAsync();
            throw;
        }
    }

    private Task ResumeBisectAsync() => RunBisectLoopAsync();

    private async Task AbortBisectAsync()
    {
        if (_bisectService?.Current is null || !await ConfirmBisect("Bisect.AbortTitle",
            localization["Bisect.AbortConfirm"]))
            return;
        await CancelBisectAsync();
    }

    private async Task RunBisectLoopAsync()
    {
        var service = _bisectService!;
        try
        {
            while (service.Current is { } session)
            {
                if (session.Candidates.Count == 0)
                    break;
                if (session.Candidates.Count == 1)
                {
                    var sole = await service.PrepareSingleVerificationAsync();
                    _bisectPage?.Refresh();
                    if (!await DeployBisectAsync())
                    {
                        await CancelBisectAsync();
                        return;
                    }
                    var singleReport = await AskBisectReport(localization["Bisect.SingleVerifyMessage"]
                        .Replace("{name}", sole.Manifest.Name));
                    if (singleReport == BisectReport.Cancel)
                    {
                        await CancelBisectAsync();
                        return;
                    }
                    if (singleReport == BisectReport.NotCrashed)
                        break;
                    if (singleReport == BisectReport.Crashed)
                        await service.DisableSuspectAsync();
                    _bisectPage?.Refresh();

                    var remaining = service.GetRemainingEnabledMods();
                    if (remaining.Count <= 1)
                        break;
                    var continueMessage = localization["Bisect.ContinueQuestion"]
                        .Replace("{name}", sole.Manifest.Name)
                        .Replace("{count}", remaining.Count.ToString());
                    if (!await ConfirmBisect("Bisect.ContinueTitle", continueMessage))
                        break;
                    await service.PrepareRemainingVerificationAsync(remaining);
                    if (!await DeployBisectAsync())
                    {
                        await CancelBisectAsync();
                        return;
                    }
                    var verifyMessage = localization["Bisect.VerifyRemainingMessage"]
                        .Replace("{count}", remaining.Count.ToString())
                        .Replace("{names}", string.Join("\n", remaining.Select(mod => mod.Manifest.Name)));
                    var verifyReport = await AskBisectReport(verifyMessage);
                    if (verifyReport == BisectReport.Cancel)
                    {
                        await CancelBisectAsync();
                        return;
                    }
                    if (verifyReport == BisectReport.NotCrashed)
                        break;
                    service.ContinueWithRemaining(remaining);
                    _bisectPage?.Refresh();
                    continue;
                }

                var round = await service.PrepareRoundAsync();
                _bisectPage?.Refresh();
                if (!await DeployBisectAsync())
                {
                    await CancelBisectAsync();
                    return;
                }
                var message = localization["Bisect.ReportMessage"]
                    .Replace("{count}", round.TestedMods.Count.ToString())
                    .Replace("{names}", string.Join("\n", round.TestedMods.Select(mod => mod.Manifest.Name)));
                var report = await AskBisectReport(message);
                if (report == BisectReport.Cancel)
                {
                    await CancelBisectAsync();
                    return;
                }
                service.ApplyResult(report == BisectReport.Crashed, round);
                _bisectPage?.Refresh();
            }

            if (service.Current is { } completed)
            {
                await service.FinishAsync(true);
                workspace.RefreshTags();
                _bisectPage?.Refresh();
                var names = string.Join("\n", completed.Suspects.Select(guid =>
                    completed.AllMods.FirstOrDefault(mod => mod.Manifest.Guid == guid)
                        ?.Manifest.Name ?? guid.ToString()));
                var summary = completed.Suspects.Count == 0
                    ? localization["Bisect.SummaryNone"]
                    : localization["Bisect.SummaryMessage"]
                        .Replace("{rounds}", completed.Rounds.Count.ToString())
                        .Replace("{count}", completed.Suspects.Count.ToString())
                        .Replace("{names}", names);
                ShowBisectMessage(localization["Bisect.SummaryTitle"], summary);
            }
        }
        catch
        {
            await RecoverBisectAsync();
            throw;
        }
    }

    private async Task<bool> DeployBisectAsync()
    {
        while (new GameProcessService(NullLogger<GameProcessService>.Instance).IsGameRunning())
            if (!await ConfirmBisect("Bisect.GameRunningTitle", localization["Bisect.GameRunningMessage"]))
                return false;
        var title = localization["Bisect.Deploying"];
        var messageBox = _messageBoxOverlay;
        if (messageBox is null)
            return false;
        messageBox.ShowProgress(title, localization["SettingsPage.PleaseWait"]);
        var count = workspace.Groups.FilterMods(workspace.Mods).Count(mod => mod.Enabled);
        var completed = 0;
        BackgroundTaskItem? task = null;
        try
        {
            await _tasks!.RunAsync(localization["DashboardPage.DeployMods"],
                localization["SettingsPage.PleaseWait"],
                (context, cancellationToken) => _bisectService!.DeployAsync(
                    step =>
                    {
                        context.ReportStep(step);
                        _ = _dispatcher!.BeginInvoke(() => messageBox.ReportProgressStep(step));
                    },
                    detail =>
                    {
                        context.ReportStepDetail(detail);
                        _ = _dispatcher!.BeginInvoke(() => messageBox.UpdateProgress(detail, null));
                    },
                    () =>
                    {
                        context.CompleteStep();
                        _ = _dispatcher!.BeginInvoke(() =>
                        {
                            messageBox.CompleteProgressStep();
                            messageBox.UpdateProgress(localization["SettingsPage.PleaseWait"],
                                count == 0 ? 1 : Math.Min(1, (double)++completed / count));
                        });
                    },
                    () =>
                    {
                        context.FailStep();
                        _ = _dispatcher!.BeginInvoke(messageBox.FailProgressStep);
                    }),
                localization["DashboardPage.DeploySuccess"],
                taskCreated: created => task = created, isForeground: true);
            await DrainUiQueueAsync();
            messageBox.CloseProgress();
            try { OpenLink("steam://run/553850"); }
            catch (Exception ex) { ReportError(ex); }
            return true;
        }
        catch (Exception ex)
        {
            await DrainUiQueueAsync();
            messageBox.CloseProgress();
            ShowModOperationResult(title, ex.Message, task?.Steps, error: true);
            return false;
        }
    }

    private async Task CancelBisectAsync()
    {
        if (_bisectService?.Current is null)
            return;
        await _bisectService.FinishAsync(false);
        workspace.RefreshTags();
        _bisectPage?.Refresh();
        ShowBisectNotice("Bisect.Title", "Bisect.CanceledMessage");
    }

    private async Task RecoverBisectAsync()
    {
        if (_bisectService?.Current is null)
            return;
        await _bisectService.FinishAsync(false);
        workspace.RefreshTags();
        _bisectPage?.Refresh();
    }

    private async Task<bool> ConfirmBisect(string titleKey, string message)
        => await _messageBoxOverlay!.ChooseOneAsync(localization[titleKey], message,
            [localization["Common.Cancel"], localization["Common.Confirm"]]) == 1;

    private async Task<BisectReport> AskBisectReport(string message)
    {
        var selected = await _messageBoxOverlay!.ChooseOneAsync(localization["Bisect.ReportTitle"], message,
            [localization["Bisect.Crashed"], localization["Bisect.NotCrashed"], localization["Bisect.Cancel"]]);
        return selected switch
        {
            0 => BisectReport.Crashed,
            1 => BisectReport.NotCrashed,
            _ => BisectReport.Cancel,
        };
    }

    private void ShowBisectNotice(string titleKey, string messageKey)
        => ShowBisectMessage(localization[titleKey], localization[messageKey]);

    private void ShowBisectMessage(string title, string message)
        => _messageBoxOverlay?.ShowInfo(title, message);

    private enum BisectReport { Crashed, NotCrashed, Cancel }
}
