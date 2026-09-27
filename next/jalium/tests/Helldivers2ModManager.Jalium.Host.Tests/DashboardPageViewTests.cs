using System.Runtime.CompilerServices;
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Jalium.Host;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Helldivers2ModManager.Jalium.Host.Tests;

[TestClass]
public sealed class DashboardPageViewTests
{
    [TestMethod]
    public async Task Dashboard_HasOriginalMainRegionsAndBindsRealMods()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-dashboard-view-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance,
                Path.Combine(root, "settings.json"), root);
            settings.InitDefault();
            settings.StorageDirectory = root;
            var modDirectory = Directory.CreateDirectory(Path.Combine(root, "Mods", "Alpha"));
            ModManifest.SaveToFile(new LegacyModManifest
            {
                Guid = Guid.NewGuid(), Name = "Alpha", Description = "A real mod",
            }, modDirectory);
            var localization = new LocalizationService(NullLogger<LocalizationService>.Instance, GetLanguageDirectory());
            localization.SelectedLanguage = "zh-CN";
            var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, database);
            var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
            var enabledRepository = new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database);
            var catalog = new ModCatalogService(NullLogger<ModCatalogService>.Instance);
            var library = new DashboardLibraryService(catalog, enabledRepository,
                NullLogger<DashboardLibraryService>.Instance);
            var workspace = await DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository);
            var actions = new RecordingActions();
            using var view = new DashboardPageView(workspace, localization, actions, ex => throw ex);
            foreach (var target in new[] { "AddCreatePanel", "TopActionBar",
                         "VersionCheckPanel", "BottomActionButtons", "BackgroundTasksButton",
                         "ArmorReuseButton", "PatchResourceViewerButton", "BisectButton",
                         "TagManagementButton", "SettingsButton" })
                Assert.IsNotNull(view.GetTutorialTarget(target), target);

            Assert.AreEqual(3, view.RowDefinitions.Count);
            Assert.AreEqual(240, view.ColumnDefinitions[0].Width.Value);
            Assert.AreEqual(6, view.Children.Count);
            var top = (Border)view.Children[1];
            var topGrid = top.Child as Grid;
            Assert.IsNotNull(topGrid);
            Assert.AreEqual(2, topGrid.RowDefinitions.Count);
            var list = (ListBox)view.Children[2];
            Assert.AreEqual(1, workspace.Rows.Count);
            Assert.AreSame(workspace.Rows, list.ItemsSource);
            var rowHost = list.ItemTemplate!.LoadContent() as Border;
            Assert.IsNotNull(rowHost);
            rowHost.DataContext = workspace.Rows[0];
            var card = rowHost.Child as Border;
            Assert.IsNotNull(card);
            var cardGrid = card.Child as Grid;
            Assert.IsNotNull(cardGrid);
            Assert.AreEqual(6, cardGrid.ColumnDefinitions.Count);
            var optionActions = cardGrid.Children.OfType<StackPanel>()
                .Single(panel => Grid.GetColumn(panel) == 2);
            Assert.AreEqual(2, Grid.GetColumn(optionActions));
            Assert.IsInstanceOfType<ComboBox>(optionActions.Children[0]);
            Assert.IsInstanceOfType<StackPanel>(optionActions.Children[1]);
            Assert.IsTrue(view.HandleKeyboardShortcut(Key.A, ModifierKeys.Control));
            Assert.AreEqual(1, workspace.SelectedCount);
            Assert.IsTrue(view.HandleKeyboardShortcut(Key.Escape, ModifierKeys.None));
            Assert.AreEqual(0, workspace.SelectedCount);
            Assert.IsTrue(view.HandleCardSelection(workspace.Mods[0], false, ModifierKeys.Control));
            Assert.AreEqual(1, workspace.SelectedCount);
            Assert.IsTrue(view.HandleCardSelection(workspace.Mods[0], false, ModifierKeys.Control));
            Assert.AreEqual(0, workspace.SelectedCount);
            Assert.IsFalse(view.HandleCardSelection(workspace.Mods[0], false, ModifierKeys.None));
            Assert.AreEqual(0, workspace.SelectedCount);
            Assert.IsFalse(view.HandleCardSelection(workspace.Mods[0], true, ModifierKeys.Control));
            Assert.AreEqual(0, workspace.SelectedCount);
            var badges = ((Grid)cardGrid.Children[0]).Children.OfType<Border>().ToArray();
            Assert.AreEqual(2, badges.Length);
            var conflictBadge = badges[0];
            var versionBadge = badges[1];
            conflictBadge.DataContext = workspace.Mods[0];
            versionBadge.DataContext = workspace.Mods[0];
            Assert.AreEqual(global::Jalium.UI.Visibility.Collapsed, conflictBadge.Visibility);
            Assert.AreEqual(global::Jalium.UI.Visibility.Collapsed, versionBadge.Visibility);
            view.SetVersionResults(new Dictionary<Guid, ModVersionCheckResult>
            {
                [workspace.Mods[0].Manifest.Guid] = new() { Status = ModVersionStatus.Incompatible },
            });
            Assert.AreEqual(global::Jalium.UI.Visibility.Visible, versionBadge.Visibility);
            view.SetVersionScanning(true);
            Assert.AreEqual(global::Jalium.UI.Visibility.Collapsed, versionBadge.Visibility);
            view.SetVersionScanning(false);
            Assert.AreEqual(global::Jalium.UI.Visibility.Visible, versionBadge.Visibility);
            view.SetConflictResult(new ModConflictAnalysisResult());
            Assert.AreEqual(global::Jalium.UI.Visibility.Visible, conflictBadge.Visibility);
            Assert.AreEqual("\u2713", ((TextBlock)conflictBadge.Child!).Text);
            var participant = new ModConflictParticipant
            {
                ModGuid = workspace.Mods[0].Manifest.Guid, ModName = "Alpha",
                PatchFileName = "0011223344556677.patch_0", UnitId = 1,
                Version = 1, DataSize = 48, GpuSize = 0, DeploymentOrder = 0,
            };
            view.SetConflictResult(new ModConflictAnalysisResult
            {
                Conflicts = [new ModConflictRecord
                {
                    UnitId = 1, FriendlyName = "Armor", OriginalName = "0x0000000000000001",
                    Participants = [participant],
                }],
            });
            Assert.AreEqual("!", ((TextBlock)conflictBadge.Child!).Text);
            view.SetConflictResult(null);
            Assert.AreEqual(global::Jalium.UI.Visibility.Collapsed, conflictBadge.Visibility);

            var navigation = (Border)view.Children[0];
            var navigationGrid = navigation.Child as Grid;
            Assert.IsNotNull(navigationGrid);
            var socialButtons = (StackPanel)navigationGrid.Children[2];
            Assert.AreEqual(2, socialButtons.Children.Count);
            Assert.IsInstanceOfType<Image>(((Button)socialButtons.Children[0]).Content);
            Assert.IsInstanceOfType<Image>(((Button)socialButtons.Children[1]).Content);
            Assert.AreEqual(localization["DashboardPage.GitHub"], ((Button)socialButtons.Children[0]).ToolTip);

            var searchArea = topGrid.Children[0] as Grid;
            Assert.IsNotNull(searchArea);
            var search = (TextBox)searchArea.Children[0];
            search.Text = "absent";
            Assert.AreEqual(0, workspace.Rows.Count);
            search.Text = "Alpha";
            Assert.AreEqual(1, workspace.Rows.Count);

            settings.UseDeploymentOrder = true;
            var savedGameDirectory = Path.Combine(root, "saved-game");
            settings.GameDirectory = savedGameDirectory;
            await settings.SaveAsync();
            settings.GameDirectory = Path.Combine(root, "draft-game");
            settings.TempDirectory = Path.Combine(root, "temp");
            Directory.CreateDirectory(Path.Combine(settings.GameDirectory, "data"));
            using var runtime = new DashboardRuntime(settings, localization, database, workspace,
                () => DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository),
                Path.Combine(root, "settings.json"));
            var persistedSettings = await runtime.LoadPersistedSettingsAsync();
            Assert.AreEqual(savedGameDirectory, persistedSettings.GameDirectory);
            Assert.AreNotEqual(settings.GameDirectory, persistedSettings.GameDirectory);
            var window = runtime.CreateWindow();
            var windowRoot = window.Content as Border;
            Assert.IsNotNull(windowRoot);
            var windowGrid = windowRoot.Child as Grid;
            Assert.IsNotNull(windowGrid);
            var presenter = windowGrid.Children[2] as ContentPresenter;
            Assert.IsNotNull(presenter);
            Assert.IsInstanceOfType<ImagePreviewOverlay>(windowGrid.Children[3]);
            Assert.IsInstanceOfType<VersionCheckDetailOverlay>(windowGrid.Children[4]);
            Assert.IsInstanceOfType<ModConflictDetailOverlay>(windowGrid.Children[5]);
            var messageOverlay = windowGrid.Children[6] as MessageBoxOverlay;
            Assert.IsNotNull(messageOverlay);
            Assert.IsInstanceOfType<FirstRunTutorialOverlay>(windowGrid.Children[7]);
            Assert.IsInstanceOfType<DashboardPageView>(presenter.Content);
            var deleteTask = runtime.ExecuteAsync(DashboardAction.DeleteMod, workspace.Mods[0]);
            Assert.IsTrue(messageOverlay.IsOpen);
            var messageDialog = (Border)messageOverlay.Children[0];
            var messageLayout = (Grid)messageDialog.Child!;
            var messageButtons = (StackPanel)messageLayout.Children[2];
            ((Button)messageButtons.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await deleteTask;
            Assert.IsTrue(modDirectory.Exists);
            await runtime.ExecuteAsync(DashboardAction.ScanConflicts);
            using (var connection = database.OpenConnection(root))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM conflict_scan_cache";
                Assert.AreEqual(1L, command.ExecuteScalar());
            }
            await runtime.ExecuteAsync(DashboardAction.ConflictDetail, workspace.Mods[0]);
            var conflictOverlay = windowGrid.Children[5] as ModConflictDetailOverlay;
            Assert.IsNotNull(conflictOverlay);
            Assert.AreEqual(global::Jalium.UI.Visibility.Visible, conflictOverlay.Visibility);
            conflictOverlay.Close();
            await runtime.ExecuteAsync(DashboardAction.CheckVersion);
            string previousModTime;
            using (var connection = database.OpenConnection(root))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ModLastWriteTimeUtc FROM version_check_results";
                previousModTime = (string)command.ExecuteScalar()!;
            }
            File.SetLastWriteTimeUtc(Path.Combine(modDirectory.FullName, "manifest.json"),
                DateTime.UtcNow.AddMinutes(2));
            await runtime.CheckVersionChangesAsync();
            using (var connection = database.OpenConnection(root))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT ModLastWriteTimeUtc FROM version_check_results";
                Assert.AreNotEqual(previousModTime, (string)command.ExecuteScalar()!);
            }
            await runtime.ConflictHashMigrationTask;
            await runtime.ExecuteAsync(DashboardAction.DeploymentOrder);
            Assert.IsInstanceOfType<DeploymentOrderPageView>(presenter.Content);
            await runtime.ExecuteAsync(DashboardAction.Settings);
            var settingsPage = presenter.Content as SettingsPageView;
            Assert.IsNotNull(settingsPage);
            var tabs = (StackPanel)((Border)settingsPage.Children[1]).Child!;
            Assert.AreEqual(7, tabs.Children.Count);
            settingsPage.SelectTab(4);
            var toolsScroll = (ScrollViewer)((Border)settingsPage.Children[2]).Child!;
            var toolsPage = (StackPanel)toolsScroll.Content!;
            var tools = (StackPanel)((Border)toolsPage.Children[0]).Child!;
            var toolCommands = tools.Children.OfType<Button>().ToArray();
            Assert.AreEqual(4, toolCommands.Length);
            await runtime.ExecuteAsync(DashboardAction.CreateMod);
            var createPage = presenter.Content as CreatePageView;
            Assert.IsNotNull(createPage);
            Assert.AreEqual(4, createPage.RowDefinitions.Count);
            await runtime.ExecuteAsync(DashboardAction.NexusDownload);
            Assert.IsInstanceOfType<NexusDownloadPageView>(presenter.Content);
            await runtime.ExecuteAsync(DashboardAction.Help);
            Assert.IsInstanceOfType<HelpPageView>(presenter.Content);
            await runtime.ExecuteAsync(DashboardAction.Bisect);
            var bisectPage = presenter.Content as BisectPageView;
            Assert.IsNotNull(bisectPage);
            Assert.AreEqual(3, bisectPage.RowDefinitions.Count);
            workspace.SetMode(DashboardCatalogMode.Workspace);
            workspace.SelectAll();
            await runtime.ExecuteAsync(DashboardAction.DeleteMod);
            Assert.AreEqual(0, workspace.Rows.OfType<ModData>().Count());
            settings.DeleteToRecycleBin = false;
            workspace.SetMode(DashboardCatalogMode.Library);
            workspace.SelectAll();
            Assert.AreEqual(1, workspace.SelectedMods.Count);
            var batchDeleteTask = runtime.ExecuteAsync(DashboardAction.DeleteMod);
            Assert.IsTrue(messageOverlay.IsOpen);
            var confirmText = ((TextBlock)((ScrollViewer)messageLayout.Children[1]).Content!).Text;
            Assert.AreEqual(localization["DashboardPage.BatchDeleteConfirm"].Replace("{count}", "1")
                + localization["DashboardPage.PermanentDeleteConfirm"], confirmText);
            ((Button)messageButtons.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await batchDeleteTask;
            Assert.IsFalse(Directory.Exists(modDirectory.FullName));
        }
        finally
        {
            database.Dispose();
            SqliteConnection.ClearAllPools();
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd2mm-jalium-dashboard-view-tests"))
                + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class RecordingActions : IDashboardActionHandler
    {
        public Task ExecuteAsync(DashboardAction action, ModData? mod = null) => Task.CompletedTask;
    }

    private static string GetLanguageDirectory([CallerFilePath] string sourceFile = "")
    {
        for (DirectoryInfo? current = new(Path.GetDirectoryName(sourceFile)!); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Helldivers2ModManager.sln")))
                return Path.Combine(current.FullName, "src", "Helldivers2ModManager", "Resources", "Language");
        throw new DirectoryNotFoundException("Repository language files not found.");
    }
}
