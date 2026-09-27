using System.Diagnostics;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.AI;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Media.Imaging;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
#if DEBUG
        if (args is ["--visual-check", var page, var screenshotPath])
            return PatchViewerVisualCheck.Run(page, screenshotPath);
#endif
        var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helldivers2ModManagerJalium");
        var dataRoot = Path.Combine(appRoot, "data");
        Directory.CreateDirectory(dataRoot);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(appRoot, "settings.json"), appRoot);
        if (!settings.InitAsync().GetAwaiter().GetResult())
        {
            settings.InitDefault();
            settings.StorageDirectory = dataRoot;
            settings.TempDirectory = Path.Combine(appRoot, "temp");
            settings.SaveAsync().GetAwaiter().GetResult();
        }

        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = settings.Language;
        using var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance),
            new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database),
            NullLogger<DashboardLibraryService>.Instance);
        var workspace = DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository)
            .GetAwaiter().GetResult();
        using var runtime = new DashboardRuntime(settings, localization, database, workspace,
            () => DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository),
            Path.Combine(appRoot, "settings.json"));
        var builder = AppBuilder.CreateBuilder(args);
        builder.ConfigureApplication(app => app.MainWindow = runtime.CreateWindow());
        using var jalium = builder.Build();
        return jalium.Run();
    }
}

internal sealed partial class DashboardRuntime(
    SettingsService settings,
    LocalizationService localization,
    DatabaseService database,
    DashboardWorkspace initialWorkspace,
    Func<Task<DashboardWorkspace>> reopenWorkspace,
    string? settingsFilePath = null) : IDashboardActionHandler, IDisposable
{
    private DashboardWorkspace workspace = initialWorkspace;
    private DashboardPageView? _dashboard;
    private SettingsPageView? _settingsPage;
    private AutoTagPairingEditor? _pairingEditor;
    private AutoTagPairingPageView? _pairingPage;
    private DeploymentOrderPageView? _orderPage;
    private DeploymentOrderEditor? _orderEditor;
    private TagManagementEditor? _tagEditor;
    private TagManagementPageView? _tagPage;
    private BackgroundTaskService? _tasks;
    private readonly ModTypeDetectionService _detection = new(NullLogger<ModTypeDetectionService>.Instance);
    private readonly AiTranslationService _translation = new(settings, NullLogger<AiTranslationService>.Instance);
    private CancellationTokenSource? _autoTagCancellation;
    private Task? _autoTagTask;
    private BackgroundTasksPageView? _tasksPage;
    private ArmorReusePageView? _armorReusePage;
    private PatchResourceViewerPageView? _patchViewerPage;
    private ModelPreviewPageView? _modelPreviewPage;
    private NexusDownloadPageView? _nexusPage;
    private CreatePageView? _createPage;
    private EditPageView? _editPage;
    private ManifestEditPageView? _manifestPage;
    private ManifestEditor? _manifestEditor;
    private FloatingMusicPlayerView? _musicPlayer;
    private HelpPageView? _helpPage;
    private FirstRunTutorialOverlay? _tutorialOverlay;
    private ImagePreviewOverlay? _imagePreviewOverlay;
    private MessageBoxOverlay? _messageBoxOverlay;
    private ModConflictDetailOverlay? _conflictOverlay;
    private VersionCheckDetailOverlay? _versionOverlay;
    private ToastOverlay? _toastOverlay;
    private VersionCheckService? _versionService;
    private VersionCheckRepository? _versionRepository;
    private MainWindowLayout? _layout;
    private Dispatcher? _dispatcher;
    private bool _modOperationActive;

    public Window CreateWindow()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcher = dispatcher;
        _tasks = new BackgroundTaskService(action => dispatcher.BeginInvoke(action), dispatcher.CheckAccess);
        _musicPlayer = new FloatingMusicPlayerView(
            new BackgroundMusicService(NullLogger<BackgroundMusicService>.Instance), settings, localization);
        _imagePreviewOverlay = new ImagePreviewOverlay(localization["MainWindow.Close"]);
        _messageBoxOverlay = new MessageBoxOverlay(localization);
        _dashboard = new DashboardPageView(workspace, localization, this, ReportError, ShowImagePreview);
        var labels = new MainWindowLabels("Helldivers 2 Mod Manager", "Jalium",
            localization["MainWindow.ReportBug"], localization["MainWindow.Help"],
            localization["MainWindow.Minimize"], localization["MainWindow.Maximize"],
            localization["MainWindow.Close"], localization["MainWindow.DropImportHint"],
            localization["MainWindow.DropImportInvalid"]);
        _tutorialOverlay = new FirstRunTutorialOverlay(localization,
            name => _dashboard?.GetTutorialTarget(name), CompleteTutorialAsync, ReportError);
        _conflictOverlay = new ModConflictDetailOverlay(localization);
        _toastOverlay = new ToastOverlay(dispatcher);
        _versionOverlay = new VersionCheckDetailOverlay(localization, settings,
            () => _versionService, RefreshVersionWorkspaceAsync, () => _layout?.Window, _messageBoxOverlay);
        Panel.SetZIndex(_tutorialOverlay, 110);
        var overlays = Enumerable.Range(0, 6)
            .Select(index => index switch
            {
                0 => (UIElement)_imagePreviewOverlay,
                1 => _versionOverlay,
                2 => (UIElement)_conflictOverlay,
                3 => _messageBoxOverlay,
                4 => _tutorialOverlay,
                5 => _toastOverlay,
                _ => new Border { Visibility = Visibility.Collapsed },
            }).ToArray();
        _layout = new MainWindowLayout(_dashboard,
            BitmapImage.FromFile(Path.Combine(AppContext.BaseDirectory, "Images", "logo_icon.png")),
            labels, OpenHelp,
            () => OpenLink("https://github.com/TYHH100/Helldivers2ModManager/issues"), overlays,
            musicPlayer: _musicPlayer,
            backgroundImage: LoadBackground(), backgroundImageOpacity: settings.BackgroundOpacity);
        _layout.Window.AllowDrop = true;
        _layout.Window.PreviewDragOver += OnImportDragOver;
        _layout.Window.PreviewDrop += OnImportDrop;
        _layout.Window.DragLeave += OnImportDragLeave;
        _layout.Window.Loaded += (_, _) =>
        {
            StartAutoTag();
            RequestAutomaticConflictScan();
            RequestAutomaticVersionCheck();
            if (!settings.FirstRunTutorialCompleted)
                _ = dispatcher.BeginInvoke(StartTutorial);
        };
        _layout.Window.Closed += (_, _) =>
        {
            CancelAutoTag();
            StopConflictScanning();
            StopVersionScanning();
            _versionOverlay?.Dispose();
            _messageBoxOverlay?.Dispose();
        };
        AttachConflictWorkspace();
        AttachVersionWorkspace();
        return _layout.Window;
    }

    public async Task ExecuteAsync(DashboardAction action, ModData? mod = null)
    {
        switch (action)
        {
            case DashboardAction.AddMod:
                await ChooseAndImportArchivesAsync();
                break;
            case DashboardAction.CreateMod:
                OpenCreateMod();
                break;
            case DashboardAction.NexusDownload:
                OpenNexusDownload();
                break;
            case DashboardAction.EditMod:
                OpenEditMod(mod);
                break;
            case DashboardAction.EditManifest:
                OpenManifestEdit(mod);
                break;
            case DashboardAction.MoveToTop:
            case DashboardAction.MoveToBottom:
            case DashboardAction.MoveToPosition:
            case DashboardAction.OpenFileLocation:
            case DashboardAction.EditTags:
            case DashboardAction.RemoveFromGroup:
            case DashboardAction.EditName:
            case DashboardAction.EditDescription:
            case DashboardAction.EditImage:
            case DashboardAction.OpenModLink:
            case DashboardAction.EditLink:
            case DashboardAction.ExportMod:
            case DashboardAction.UpdateMod:
                if (mod is not null)
                    await ExecuteModActionAsync(action, mod);
                break;
            case DashboardAction.DeleteMod:
                await DeleteModAsync(mod);
                break;
            case DashboardAction.Settings:
                await OpenSettingsAsync();
                break;
            case DashboardAction.DeploymentOrder:
                OpenDeploymentOrder();
                break;
            case DashboardAction.TagManagement:
                OpenTagManagement();
                break;
            case DashboardAction.BackgroundTasks:
                OpenBackgroundTasks();
                break;
            case DashboardAction.Help:
                OpenHelp();
                break;
            case DashboardAction.Bisect:
                await OpenBisectAsync();
                break;
            case DashboardAction.ArmorReuse:
                OpenArmorReuse();
                break;
            case DashboardAction.PatchResourceViewer:
                OpenPatchResourceViewer(mod);
                break;
            case DashboardAction.PreviewModel:
                OpenModelPreview(mod);
                break;
            case DashboardAction.ScanConflicts:
                await ScanConflictsAsync(showReport: true);
                break;
            case DashboardAction.CheckVersion:
                await ScanVersionsAsync();
                break;
            case DashboardAction.VersionDetail:
                await OpenVersionDetailAsync(mod);
                break;
            case DashboardAction.BatchRepair:
                await BatchRepairAsync();
                break;
            case DashboardAction.ConflictDetail:
                OpenConflictDetail(mod);
                break;
            case DashboardAction.BatchTag:
                await OpenBatchTagsAsync();
                break;
            case DashboardAction.AddToGroup:
                await OpenGroupSelectionAsync(mod);
                break;
            case DashboardAction.Github:
                OpenLink("https://github.com/teutinsa/Helldivers2ModManager");
                break;
            case DashboardAction.GithubFork:
                OpenLink("https://github.com/TYHH100/Helldivers2ModManager");
                break;
            case DashboardAction.Discord:
                OpenLink("https://discord.gg/helldiversmodding");
                break;
            case DashboardAction.LaunchGame:
                OpenLink("steam://run/553850");
                break;
            case DashboardAction.Rescan:
                await CancelAutoTagAsync();
                await workspace.RefreshAsync();
                StartAutoTag();
                break;
            case DashboardAction.CreateSeparator:
                await workspace.CreateSeparatorAsync(localization["DashboardPage.DefaultSeparatorName"]);
                break;
            case DashboardAction.Deploy:
                await DeployModsAsync();
                break;
            case DashboardAction.Purge:
                await PurgeModsAsync();
                break;
            default:
                throw new NotSupportedException(localization["JaliumMigration.Unavailable"]);
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        await CancelAutoTagAsync();
        await workspace.SaveCurrentAsync();
        var editor = new SettingsEditor(settings, localization);
        _settingsPage = new SettingsPageView(editor, localization, _layout.Window,
            async () =>
            {
                await editor.SaveAsync();
                _musicPlayer?.SetEnabled(settings.EnableMusicPlayer, settings.AutoPlayBackgroundMusic);
                await ReopenDashboardAsync();
            },
            async () =>
            {
                await editor.CancelAsync();
                _musicPlayer?.SetEnabled(settings.EnableMusicPlayer, settings.AutoPlayBackgroundMusic);
                await ReopenDashboardAsync();
            }, OpenAutoTagPairing, async () =>
            {
                await editor.CancelAsync();
                await ReopenDashboardAsync();
                StartTutorial();
            }, RecomputeHashesAsync, HardPurgeAsync, ResetSettingsAsync, _messageBoxOverlay!, ReportError,
            enabled => _musicPlayer?.SetEnabled(enabled, settings.AutoPlayBackgroundMusic));
        _layout.PagePresenter.Content = _settingsPage;
    }

    private void OpenAutoTagPairing()
    {
        if (_layout is null || _settingsPage is null)
            throw new InvalidOperationException("Settings page is not ready.");
        _pairingEditor = new AutoTagPairingEditor(settings, localization);
        _pairingPage = new AutoTagPairingPageView(_pairingEditor, localization,
            _messageBoxOverlay!, ReturnToSettings, ReportError);
        _layout.PagePresenter.Content = _pairingPage;
    }

    private void ReturnToSettings()
    {
        if (_layout is null || _settingsPage is null)
            return;
        _layout.PagePresenter.Content = _settingsPage;
        _pairingPage?.Dispose();
        _pairingEditor?.Dispose();
        _pairingPage = null;
        _pairingEditor = null;
    }

    private async Task ReopenDashboardAsync()
    {
        await CancelAutoTagAsync();
        CancelConflictScan();
        CancelVersionScan();
        workspace.ProfileChanged -= OnConflictProfileChanged;
        var refreshed = await reopenWorkspace();
        var dashboard = new DashboardPageView(refreshed, localization, this, ReportError, ShowImagePreview);
        var previous = _dashboard;
        workspace = refreshed;
        _dashboard = dashboard;
        _layout?.SetBackground(LoadBackground(), settings.BackgroundOpacity);
        AttachConflictWorkspace();
        AttachVersionWorkspace();
        ShowDashboard();
        previous?.Dispose();
        StartAutoTag();
        RequestAutomaticConflictScan();
        RequestAutomaticVersionCheck();
    }

    private async Task RefreshVersionWorkspaceAsync()
    {
        _versionCancellation?.Cancel();
        await _versionIdleTask;
        await workspace.RefreshAsync();
        await ScanVersionsAsync(afterRepair: true);
    }

    private void StartAutoTag() => _autoTagTask = AutoTagCurrentAsync();

    private async Task AutoTagCurrentAsync()
    {
        if (!settings.EnableAutoTagging || settings.IsReadonly || workspace.Mods.Count == 0 || _tasks is null)
            return;
        var cancellation = new CancellationTokenSource();
        _autoTagCancellation = cancellation;
        var targetWorkspace = workspace;
        var targets = targetWorkspace.Mods.ToArray();
        try
        {
            var detections = await _tasks.RunAsync(
                localization["DashboardPage.AutoTagTitle"],
                localization["SettingsPage.PleaseWait"],
                (_, token) => Task.FromResult(_detection.DetectAll(targets, token)),
                cancellationToken: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(targetWorkspace, workspace))
                return;
            await targetWorkspace.ApplyAutoTagsAsync(_detection, localization, detections,
                cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            if (ReferenceEquals(_autoTagCancellation, cancellation))
                _autoTagCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelAutoTag()
    {
        _autoTagCancellation?.Cancel();
        _autoTagCancellation = null;
    }

    private async Task CancelAutoTagAsync()
    {
        CancelAutoTag();
        if (_autoTagTask is { } pending)
            await pending;
        _autoTagTask = null;
    }

    private BitmapImage? LoadBackground()
    {
        if (settings.BackgroundMode != BackgroundMode.Image
            || !File.Exists(settings.BackgroundImagePath))
            return null;
        try { return BitmapImage.FromFile(settings.BackgroundImagePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void OpenDeploymentOrder()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        _orderEditor = new DeploymentOrderEditor(settings, localization, workspace.Mods,
            () => workspace.Groups.FilterMods(workspace.Mods)
                .Select(mod => mod.Manifest.Guid).ToArray());
        _orderPage = new DeploymentOrderPageView(_orderEditor, localization, ShowDashboard, ReportError);
        _layout.PagePresenter.Content = _orderPage;
    }

    private void OpenTagManagement()
    {
        if (_layout is null)
            throw new InvalidOperationException("Window is not ready.");
        _tagEditor = new TagManagementEditor(settings);
        _tagPage = new TagManagementPageView(_tagEditor, localization, _messageBoxOverlay!,
            ShowDashboard, workspace.RefreshTags, ReportError);
        _layout.PagePresenter.Content = _tagPage;
    }

    private void OpenBackgroundTasks()
    {
        if (_layout is null || _tasks is null)
            throw new InvalidOperationException("Window is not ready.");
        _tasksPage = new BackgroundTasksPageView(_tasks, localization, ShowDashboard);
        _layout.PagePresenter.Content = _tasksPage;
    }

    private void OpenHelp()
    {
        if (_layout is null)
            return;
        _helpPage ??= new HelpPageView(localization, ShowDashboard, OpenLink);
        _layout.PagePresenter.Content = _helpPage;
    }

    private Task OpenBatchTagsAsync() => OpenBatchTagsAsync(workspace.SelectedMods);

    private async Task OpenBatchTagsAsync(IReadOnlyList<ModData> selected)
    {
        if (selected.Count == 0)
            return;
        var initial = selected[0].TagIds.ToHashSet();
        var tags = settings.Tags.ToArray();
        await _messageBoxOverlay!.SelectManyAsync(
            localization["DashboardPage.BatchTagTitle"],
            localization["DashboardPage.BatchTagPrefix"] + selected.Count
                + localization["DashboardPage.BatchTagSuffix"],
            tags.Select(tag => new MessageBoxSelectionOption(tag.Name, tag.Color)).ToArray(),
            Enumerable.Range(0, tags.Length).Where(index => initial.Contains(tags[index].Id)).ToArray(),
            async indices =>
            {
                try
                {
                    await workspace.SetTagsAsync(selected, indices.Select(index => tags[index].Id).ToArray());
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            });
    }

    private async Task OpenGroupSelectionAsync(ModData? source)
    {
        var selected = source is not null && !workspace.SelectedGuids.Contains(source.Manifest.Guid)
            ? [source]
            : workspace.Rows.OfType<ModData>()
                .Where(mod => workspace.SelectedGuids.Contains(mod.Manifest.Guid)).ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException(localization["ModGroup.NoSelectedMods"]);

        var selectedGuids = selected.Select(mod => mod.Manifest.Guid).ToHashSet();
        var groups = workspace.Groups.Groups.ToArray();
        await _messageBoxOverlay!.SelectManyAsync(
            localization["ModGroup.AddToGroup"],
            localization["ModGroup.AddToGroupsMessage"].Replace("{count}", selected.Length.ToString()),
            groups.Select(group => new MessageBoxSelectionOption(group.Name)).ToArray(),
            Enumerable.Range(0, groups.Length)
                .Where(index => selectedGuids.All(groups[index].ModGuids.Contains)).ToArray(),
            async indices =>
            {
                try
                {
                    await workspace.SetModsToGroupsAsync(indices.Select(index => groups[index].Id).ToArray(), selected);
                    return null;
                }
                catch (Exception ex) { return ex.Message; }
            });
    }

    private void ShowDashboard()
    {
        if (_layout is null)
            return;
        _layout.PagePresenter.Content = _dashboard;
        _orderPage?.Dispose();
        _orderEditor?.Dispose();
        _orderPage = null;
        _orderEditor = null;
        _tagPage?.Dispose();
        _tagEditor?.Dispose();
        _tagPage = null;
        _tagEditor = null;
        _tasksPage?.Dispose();
        _tasksPage = null;
        _armorReusePage?.Dispose();
        _armorReusePage = null;
        _patchViewerPage?.Dispose();
        _patchViewerPage = null;
        _modelPreviewPage?.Dispose();
        _modelPreviewPage = null;
        _nexusPage?.Dispose();
        _nexusPage = null;
        _createPage = null;
        _editPage?.Dispose();
        _editPage = null;
        _manifestPage?.Dispose();
        _manifestPage = null;
        _manifestEditor = null;
        _settingsPage?.Dispose();
        _settingsPage = null;
        _pairingPage?.Dispose();
        _pairingEditor?.Dispose();
        _pairingPage = null;
        _pairingEditor = null;
        _helpPage?.Dispose();
        _helpPage = null;
        _bisectPage?.Dispose();
        _bisectPage = null;
        _bisectService = null;
        _bisectModService = null;
    }

    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url)
    {
        UseShellExecute = true,
    });

    private void ReportError(Exception error)
    {
        if (_dispatcher is null || _messageBoxOverlay is null) return;
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => ReportError(error));
            return;
        }
        _messageBoxOverlay.ShowError(error.Message);
    }

    public void Dispose()
    {
        CancelAutoTag();
        if (_layout is not null)
        {
            _layout.Window.PreviewDragOver -= OnImportDragOver;
            _layout.Window.PreviewDrop -= OnImportDrop;
            _layout.Window.DragLeave -= OnImportDragLeave;
        }
        _orderPage?.Dispose();
        _orderEditor?.Dispose();
        _tagPage?.Dispose();
        _tagEditor?.Dispose();
        _tasksPage?.Dispose();
        _armorReusePage?.Dispose();
        _patchViewerPage?.Dispose();
        _modelPreviewPage?.Dispose();
        _nexusPage?.Dispose();
        _nexusCache?.Dispose();
        _createPage = null;
        _editPage?.Dispose();
        _manifestPage?.Dispose();
        _editPage = null;
        _manifestPage = null;
        _manifestEditor = null;
        _settingsPage?.Dispose();
        _pairingPage?.Dispose();
        _pairingEditor?.Dispose();
        _dashboard?.Dispose();
        _helpPage?.Dispose();
        _tutorialOverlay?.Dispose();
        StopConflictScanning();
        StopVersionScanning();
        workspace.ProfileChanged -= OnConflictProfileChanged;
        _conflictOverlay?.Dispose();
        _toastOverlay?.Dispose();
        _bisectPage?.Dispose();
        _translation.Dispose();
        _musicPlayer?.Dispose();
        _musicPlayer = null;
    }
}
