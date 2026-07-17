// Ignore Spelling: App

using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.ViewModels;
using Helldivers2ModManager.Core.Archives;
using Helldivers2ModManager.Core.Security;
using Helldivers2ModManager.Core.Settings;
using Helldivers2ModManager.Infrastructure.Archives;
using Helldivers2ModManager.Infrastructure.Security;
using Helldivers2ModManager.Infrastructure.Settings;
using Helldivers2ModManager.Core.TemporaryFiles;
using Helldivers2ModManager.Infrastructure.TemporaryFiles;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Infrastructure.Mods;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Core.Compatibility;
using Helldivers2ModManager.Infrastructure.Compatibility;
using DialogHost = Helldivers2ModManager.Components.MessageBox;
using VersionDetailHost = Helldivers2ModManager.Components.VersionCheckDetailOverlay;

namespace Helldivers2ModManager;

internal partial class App : Application
{
    public static readonly Version Version = new(2, 0, 0, 0);

    public static readonly string? VersionAddition = "";

    public new static App Current => (App)Application.Current;

    public IHost Host { get; }

    public LogLevel LogLevel { get; set; }

    private readonly ILogger? _logger;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogUnhandledException(e.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, e) => LogUnhandledException(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => LogUnhandledException(e.Exception);

        HostApplicationBuilder builder = new();

        // 注册内存缓存服务
        builder.Services.AddMemoryCache();

        AddServices(builder.Services);
        builder.Services.AddSingleton<INavigationService>(static provider =>
            new NavigationService(() => provider.GetRequiredService<NavigationStore>()));
        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<IClipboardService, WpfClipboardService>();
        builder.Services.AddSingleton<IFilePickerService, WpfFilePickerService>();
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<IBackgroundTaskRunner, WpfBackgroundTaskRunner>();
        builder.Services.AddSingleton<IPatchScanner, StingrayPatchScanner>();
        builder.Services.AddSingleton<ICompatibilityEvaluator, CompatibilityEvaluator>();
        builder.Services.AddSingleton<IVersionCheckCoordinator, VersionCheckCoordinator>();
        builder.Services.AddSingleton<IBackupStore, FileSystemBackupStore>();
        builder.Services.AddSingleton<IRepairExecutor, TransactionalBinaryRepairExecutor>();
        builder.Services.AddSingleton<NavigationStore>(static services => new NavigationStore(services, services.GetRequiredService<DashboardPageViewModel>()));
        builder.Services.AddLogging(log =>
        {
#if DEBUG
            log.SetMinimumLevel(LogLevel.Trace);
            log.AddDebug();
#endif
            log.AddConsole();
            log.AddFile("ModManager");
        });
        builder.Services.AddTransient<MainWindow>();

        Host = builder.Build();

        _logger = Host.Services.GetRequiredService<ILogger<App>>();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化 SharpSevenZip：提取嵌入式 7z.dll 并设置库路径
        InitializeSharpSevenZip();

        // 尽早加载设置并应用已保存的语言偏好（在显示主窗口之前）
        await InitializeLanguageAsync();

        MainWindow = Host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
        DialogHost.Current?.Configure(Host.Services.GetRequiredService<LocalizationService>());
        VersionDetailHost.Current?.Configure(
            Host.Services.GetRequiredService<LocalizationService>(),
            Host.Services.GetRequiredService<VersionCheckService>(),
            Host.Services.GetRequiredService<RepairDisclaimerService>(),
            Host.Services.GetRequiredService<IDialogService>(),
            Host.Services.GetRequiredService<IRepairPlanner>(),
            Host.Services.GetRequiredService<IRepairExecutor>(),
            Host.Services.GetRequiredService<ICompanionRecoveryService>(),
            Host.Services.GetRequiredService<IClipboardService>(),
            Host.Services.GetRequiredService<ILogger<Helldivers2ModManager.Components.VersionCheckDetailOverlay>>());

        var browserStartTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        browserStartTimer.Tick += (_, _) =>
        {
            browserStartTimer.Stop();
            try
            {
                var settingsService = Host.Services.GetRequiredService<SettingsService>();
                if (!settingsService.Initialized || !settingsService.EnableBrowserIntegration)
                {
                    _logger?.LogInformation("Browser extension integration is disabled");
                    return;
                }

                var browserExtensionService = Host.Services.GetRequiredService<BrowserExtensionService>();
                browserExtensionService.Start();
                _logger?.LogInformation("Browser extension service started successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to start browser extension service");
            }
        };
        browserStartTimer.Start();
    }

    /// <summary>
    /// 在启动时尽早加载设置并应用已保存的语言偏好，
    /// 避免使用自动检测（系统语言）覆盖用户手动指定的语言。
    /// 异步加载设置和语言偏好，不阻塞 WPF 首帧；SettingsService 内部保证并发初始化共享。
    /// </summary>
    private async Task InitializeLanguageAsync()
    {
        try
        {
            var settingsService = Host.Services.GetRequiredService<SettingsService>();
            var localizationService = Host.Services.GetRequiredService<LocalizationService>();

            if (await settingsService.InitAsync().ConfigureAwait(false))
            {
                var workspaceManager = Host.Services.GetRequiredService<IOperationWorkspaceManager>();
                var cleanedWorkspaceCount = workspaceManager.CleanupAbandoned(settingsService.TempDirectory);
                if (cleanedWorkspaceCount > 0)
                    _logger?.LogInformation("Cleaned {Count} abandoned owned workspaces", cleanedWorkspaceCount);

                var themeService = Host.Services.GetRequiredService<ThemeService>();
                await Dispatcher.InvokeAsync(() =>
                {
                    themeService.Apply(settingsService.Theme, settingsService.EnableAnimations);
                    if (!string.IsNullOrEmpty(settingsService.Language))
                    {
                        localizationService.SelectedLanguage = settingsService.Language;
                        _logger?.LogInformation("Applied saved language preference: {Lang}", settingsService.Language);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize language preference, using auto-detect");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Host.Services.GetRequiredService<ProfileSaveCoordinator>()
                .FlushAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to flush profile state during application exit");
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 初始化 SharpSevenZip：设置 7z.dll 库路径。
    /// 7z.dll 随程序分发（Content CopyToOutputDirectory），与应用在同一目录。
    /// </summary>
    private void InitializeSharpSevenZip()
    {
        try
        {
            var dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z.dll");
            if (!File.Exists(dllPath))
            {
                _logger?.LogWarning("7z.dll 不存在于应用目录: {Path}", dllPath);
                return;
            }

            SharpSevenZip.SharpSevenZipBase.SetLibraryPath(dllPath);
            _logger?.LogInformation("SharpSevenZip 初始化完成，7z.dll 路径: {Path}", dllPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "SharpSevenZip 初始化失败");
        }
    }

    private static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<ISafePathPolicy, Helldivers2ModManager.Infrastructure.Security.SafePathPolicy>();
        services.AddSingleton<IArchiveInspector, SafeArchiveInspector>();
        services.AddSingleton<IOperationWorkspaceManager, OperationWorkspaceManager>();
        services.AddSingleton<IModImportService, TransactionalModImportService>();
        services.AddSingleton<ISettingsStore>(_ => new AtomicJsonSettingsStore(
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
            Path.Combine(Environment.CurrentDirectory, "settings.json")));
        services.AddSingleton<ILocalizer>(static provider => provider.GetRequiredService<LocalizationService>());
        services.AddSingleton<ILocaleCatalog>(static provider => provider.GetRequiredService<LocalizationService>());

        var tuples = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Select(static type => (type, type.GetCustomAttribute<RegisterServiceAttribute>()))
            .Where(static tuple => tuple.Item2 is not null)
            .Cast<ValueTuple<Type, RegisterServiceAttribute>>()
            .ToArray();

        foreach (var (type, attr) in tuples)
        {
            switch (attr.Lifetime)
            {
                case ServiceLifetime.Singleton:
                    if (attr.Contract is null)
                    {
                        services.AddSingleton(type);
                    }
                    else
                    {
                        // 同时注册接口和具体类型（复用同一单例）
                        services.AddSingleton(type);
                        services.AddSingleton(attr.Contract, sp => sp.GetRequiredService(type));
                    }
                    break;

                case ServiceLifetime.Scoped:
                    if (attr.Contract is null)
                    {
                        services.AddScoped(type);
                    }
                    else
                    {
                        // 同时注册接口和具体类型
                        services.AddScoped(type);
                        services.AddScoped(attr.Contract, sp => sp.GetRequiredService(type));
                    }
                    break;

                case ServiceLifetime.Transient:
                    if (attr.Contract is null)
                    {
                        services.AddTransient(type);
                    }
                    else
                    {
                        // 同时注册接口和具体类型
                        services.AddTransient(type);
                        services.AddTransient(attr.Contract, sp => sp.GetRequiredService(type));
                    }
                    break;
            }
        }
    }

    private void LogUnhandledException(Exception? ex)
    {
        // Bootstrap-only fallback: the DI host and IDialogService do not exist yet.
        if (_logger is null)
            MessageBox.Show($"An unhandled exception occurred before logging could be initialized!\n\n{ex?.ToString()}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            _logger?.LogError(ex, "An unhandled exception occured!");
    }
}
