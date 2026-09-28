#if DEBUG
using Helldivers2ModManager.Jalium.Core;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Jalium.UI;
using Jalium.UI.Controls;
using Jalium.UI.Threading;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Jalium.Host;

/// <summary>
/// 页面级视觉检查：用与主入口相同的装配启动真实 runtime 窗口，
/// 导航到指定页面并截图。数据目录独立于正常 Host（避免与运行中的实例抢数据库），
/// 存储目录优先指向端到端样例库（%TEMP%/hd2mm-jalium-e2e/library）。
/// 用法：--visual-check page:&lt;DashboardAction 名&gt; &lt;截图路径&gt;
/// </summary>
internal static class PageVisualCheck
{
    public static int Run(string page, string screenshotPath)
    {
        if (!Enum.TryParse<DashboardAction>(page, out var action))
        {
            Console.Error.WriteLine($"Unknown page action: {page}");
            return 2;
        }

        var appRoot = Path.Combine(Path.GetTempPath(), "hd2mm-pagecheck");
        var dataRoot = Path.Combine(appRoot, "data");
        Directory.CreateDirectory(dataRoot);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance,
            Path.Combine(appRoot, "settings.json"), appRoot);
        if (!Task.Run(() => settings.InitAsync()).GetAwaiter().GetResult())
        {
            settings.InitDefault();
            settings.StorageDirectory = dataRoot;
            settings.TempDirectory = Path.Combine(appRoot, "temp");
            Task.Run(() => settings.SaveAsync()).GetAwaiter().GetResult();
        }

        // 有端到端样例库时指向它，让页面展示真实模组数据。
        var e2eLibrary = Path.Combine(Path.GetTempPath(), "hd2mm-jalium-e2e", "library");
        if (Directory.Exists(e2eLibrary))
            settings.StorageDirectory = e2eLibrary;
        if (action is DashboardAction.DeploymentOrder)
            settings.UseDeploymentOrder = true;
        settings.FirstRunTutorialCompleted = true; // 页面验收不需要引导层遮挡

        var localization = new LocalizationService(NullLogger<LocalizationService>.Instance,
            Path.Combine(AppContext.BaseDirectory, "Language"));
        localization.SelectedLanguage = settings.Language;
        var database = new DatabaseService(NullLogger<DatabaseService>.Instance);
        var groupRepository = new ModGroupRepository(NullLogger<ModGroupRepository>.Instance, database);
        var groups = new ModGroupService(NullLogger<ModGroupService>.Instance, groupRepository, localization);
        var library = new DashboardLibraryService(
            new ModCatalogService(NullLogger<ModCatalogService>.Instance),
            new EnabledDataRepository(NullLogger<EnabledDataRepository>.Instance, database),
            NullLogger<DashboardLibraryService>.Instance);
        var workspace = Task.Run(() => DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository))
            .GetAwaiter().GetResult();
        using var runtime = new DashboardRuntime(settings, localization, database, workspace,
            () => DashboardWorkspace.OpenAsync(settings, library, groups, groupRepository),
            Path.Combine(appRoot, "settings.json"));
        Console.Error.WriteLine($"[pagecheck] storage={settings.StorageDirectory} mods={workspace.Mods.Count} initialized={settings.Initialized}");
        var window = runtime.CreateWindow();
        var captured = false;
        window.ContentRendered += (_, _) =>
        {
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var mod = workspace.Mods.Count > 0 ? workspace.Mods[0] : null;
                    if (action is DashboardAction.EditMod or DashboardAction.EditManifest && mod is null)
                    {
                        Console.Error.WriteLine("EditMod/EditManifest require at least one mod in the library.");
                        window.Close();
                        return;
                    }
                    await runtime.ExecuteAsync(action, mod);
                    await Task.Delay(900);
                    PatchViewerVisualCheck.Capture(window.Handle, screenshotPath);
                    captured = true;
                    Console.WriteLine($"SCREENSHOT_OK {screenshotPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                }
                finally
                {
                    window.Close();
                }
            });
        };
        var builder = AppBuilder.CreateBuilder([]);
        builder.ConfigureApplication(app => app.MainWindow = window);
        using var jalium = builder.Build();
        jalium.Run();
        database.Dispose();
        try { Directory.Delete(appRoot, true); } catch { }
        return captured ? 0 : 1;
    }
}
#endif
