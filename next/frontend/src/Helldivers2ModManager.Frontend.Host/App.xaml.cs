using System.IO;
using System.Windows;
using System.Windows.Threading;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Profiles;
using Helldivers2ModManager.Frontend;
using Helldivers2ModManager.Frontend.Views;
using Helldivers2ModManager.Frontend.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Frontend.Host;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Helldivers2ModManagerNext");
        Directory.CreateDirectory(rootDirectory);

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Information));
        services.AddCommon();
        services.AddPersistence(Path.Combine(rootDirectory, "data", "mod_manager.db"));
        services.AddMods();
        services.AddProfiles();
        services.AddDeployment();
        services.AddGameData();
        services.AddSingleton(new ApplicationPaths(rootDirectory));
        services.AddFrontend();
        _services = services.BuildServiceProvider();

        await _services.GetRequiredService<ApplicationSettingsService>().InitializeAsync();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "Helldivers 2 Mod Manager — Next",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
