using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using SharpSevenZip;

namespace Helldivers2ModManager.Jalium.Host;

internal sealed partial class DashboardRuntime
{
    private static readonly HashSet<string> ArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar", ".tar" };

    private void OnImportDragOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var supported = paths is { Length: > 0 }
            && paths.All(path => File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path)));
        _layout?.SetDropHint(true, supported);
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnImportDragLeave(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            _layout?.SetDropHint(false, false);
    }

    private async void OnImportDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        _layout?.SetDropHint(false, false);
        e.Handled = true;
        if (paths is not { Length: > 0 }
            || !paths.All(path => File.Exists(path) && ArchiveExtensions.Contains(Path.GetExtension(path))))
            return;
        try { await ImportArchivesAsync(paths); }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task ChooseAndImportArchivesAsync()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Filter = localization["Common.FileFilterArchive"],
            Multiselect = true,
            Title = localization["DashboardPage.AddModDialogTitle"],
        };
        if (dialog.ShowDialog() != true)
            return;
        await ImportArchivesAsync(dialog.FileNames);
    }

    private async Task<ModImportResult?> ImportArchivesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _tasks is null || _layout is null)
            return null;
        if (settings.IsReadonly)
            throw new InvalidOperationException(localization["AutoTagPairingPage.ReadonlyError"]);

        await CancelAutoTagAsync();
        await workspace.SaveCurrentAsync();
        var nativeLibrary = Path.Combine(AppContext.BaseDirectory, "7z.dll");
        if (!File.Exists(nativeLibrary))
            throw new FileNotFoundException("7z.dll", nativeLibrary);
        SharpSevenZipBase.SetLibraryPath(nativeLibrary);

        var messageBox = _messageBoxOverlay;
        if (messageBox is null)
            return null;
        messageBox.ShowProgress(paths.Count == 1 ? localization["DashboardPage.AddSingleProgress"]
            : localization["DashboardPage.BatchAddProgressTitle"], localization["SettingsPage.PleaseWait"]);
        ModImportResult? result = null;
        string? groupWarning = null;
        try
        {
            var service = CreateModService();
            await Task.Run(() => service.Init(settings));

            var workflow = new ModImportWorkflow(service);
            result = await _tasks.RunAsync(
                localization["BackgroundTasksPage.TaskTypeImport"],
                paths.Count == 1 ? Path.GetFileName(paths[0])
                    : localization["DashboardPage.BatchAddWaitMsg"].Replace("{total}", paths.Count.ToString()),
                (context, cancellationToken) => workflow.ImportAsync(paths,
                    archiveName => WithImportProgressHiddenAsync(
                        () => RequestArchivePasswordAsync(archiveName)),
                    () => WithImportProgressHiddenAsync(ConfirmScriptImportAsync), update =>
                    {
                        var fileName = update.NestedFileName ?? update.FileName;
                        var ratio = update.NestedCount is > 0
                            ? (update.FileIndex + (double)(update.NestedIndex!.Value + 1) / update.NestedCount.Value)
                                / update.FileCount
                            : (double)update.FileIndex / update.FileCount;
                        context.Report(fileName, ratio, false);
                        _ = _dispatcher?.BeginInvoke(() => messageBox.UpdateProgress(fileName, ratio));
                    }),
                isForeground: true);

            if (settings.AutoAddImportedModsToActiveProfile && result.AddedGuids.Count > 0)
            {
                var added = result.AddedGuids.Select(service.GetModByGuid).OfType<ModData>().ToArray();
                try { await workspace.AddToGroupAsync(workspace.Groups.SelectedGroup.Id, added); }
                catch (Exception ex) { groupWarning = ex.Message; }
            }
            workspace.SetSearchText(string.Empty);
            await workspace.RefreshAsync(persistImportedDefaults: true);
            StartAutoTag();
        }
        finally
        {
            messageBox.CloseProgress();
        }
        if (result is not null && (paths.Count > 1 || result.Problems.Count > 0 || groupWarning is not null))
            ShowImportResult(result, groupWarning);
        return result;
    }

    private ModService CreateModService()
    {
        var fileHashes = new FileHashRepository(NullLogger<FileHashRepository>.Instance, database);
        var hashes = new ModHashService(NullLogger<ModHashService>.Instance,
            fileHashes, database, localization, _tasks!);
        return new ModService(NullLogger<ModService>.Instance, hashes, localization,
            new GameProcessService(NullLogger<GameProcessService>.Instance), fileHashes);
    }

    private Task<string?> RequestArchivePasswordAsync(string archiveName) => OnUiAsync(() =>
        _messageBoxOverlay!.PromptPasswordAsync(localization["DashboardPage.ArchivePasswordTitle"],
            localization["DashboardPage.ArchivePasswordMessage"].Replace("{file}", archiveName)));

    private Task<bool> ConfirmScriptImportAsync() => OnUiAsync(() =>
        _messageBoxOverlay!.ConfirmAsync(localization["DashboardPage.ScriptModSecurityWarningTitle"],
            localization["DashboardPage.ScriptModSecurityWarningMessage"]));

    private Task<T> WithImportProgressHiddenAsync<T>(Func<Task<T>> prompt) =>
        OnUiAsync(async () =>
        {
            _messageBoxOverlay!.PauseProgress();
            try { return await prompt(); }
            finally
            {
                if (_messageBoxOverlay is { IsDisposed: false })
                    _messageBoxOverlay.ResumeProgress();
            }
        });

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher is null)
            throw new InvalidOperationException("Window is not ready.");
        if (_dispatcher.CheckAccess())
            return await action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.BeginInvoke(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return await completion.Task;
    }

    private void ShowImportResult(ModImportResult result, string? groupWarning)
    {
        var summary = result.Failed > 0
            ? localization["DashboardPage.BatchAddDoneErrors"]
                .Replace("{success}", result.Succeeded.ToString())
                .Replace("{fail}", result.Failed.ToString())
            : localization["DashboardPage.BatchAddSuccess"]
                .Replace("{count}", result.Succeeded.ToString());
        var details = result.Problems.Select(problem =>
            $"{problem.Directory.Name}: {FormatImportProblem(problem)}").ToList();
        if (groupWarning is not null)
            details.Add(groupWarning);
        var message = details.Count == 0 ? summary
            : summary + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine + Environment.NewLine, details);
        _messageBoxOverlay!.ShowInfo(localization["DashboardPage.AddMod"], message);
    }

    private string FormatImportProblem(ModProblem problem) => problem.Kind switch
    {
        ModProblemKind.CantReadArchive => problem.ExtraData is null
            ? localization["DashboardPage.CantReadArchive"]
            : localization["DashboardPage.CantReadArchivePrefix"] + problem.ExtraData,
        ModProblemKind.Duplicate => localization["DashboardPage.DuplicateGuid"],
        ModProblemKind.NoManifestFound => localization["DashboardPage.NoManifestFoundInfer"],
        ModProblemKind.EmptyOptions => localization["DashboardPage.EmptyOptions"],
        ModProblemKind.EmptySubOptions => localization["DashboardPage.EmptySubOptions"],
        ModProblemKind.CantParseManifest => localization["DashboardPage.CantParseManifest"],
        ModProblemKind.UnknownManifestVersion => localization["DashboardPage.UnknownManifestVersion"],
        ModProblemKind.OutOfSupportManifest => localization["DashboardPage.OutOfSupportManifest"],
        ModProblemKind.InvalidPath => problem.ExtraData is null
            ? localization["DashboardPage.InvalidPathError"]
            : localization["DashboardPage.InvalidPathPrefix"] + problem.ExtraData
                + localization["DashboardPage.InvalidPathSuffix"],
        ModProblemKind.MissingIncludePath => problem.ExtraData is null
            ? localization["DashboardPage.MissingIncludePath"]
            : localization["DashboardPage.MissingIncludePathPrefix"] + problem.ExtraData
                + localization["DashboardPage.MissingIncludePathSuffix"],
        ModProblemKind.InvalidImagePath => problem.ExtraData is null
            ? localization["DashboardPage.InvalidImagePathError"]
            : localization["DashboardPage.InvalidImagePathPrefix"] + problem.ExtraData
                + localization["DashboardPage.InvalidPathSuffix"],
        ModProblemKind.EmptyImagePath => localization["DashboardPage.EmptyImagePath"],
        _ => problem.Kind.ToString(),
    };
}
