// Ignore Spelling: App

using System.IO;
using Helldivers2ModManager.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Windows;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.ViewModels;

namespace Helldivers2ModManager;

internal partial class App : Application
{
	public static readonly Version Version = new(1, 5, 0, 0);

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
		builder.Services.AddSingleton<NavigationStore>(static services => new NavigationStore(services));
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

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		// 闪屏：显示 LOGO，等主窗口首帧内容真正渲染完成（ContentRendered）后再关闭，
		// 避免 WPF 默认 SplashScreen 过早关闭让主窗口黑底透过 LOGO 透明区域一闪而过。
		SplashWindow? splash = null;
		try
		{
			splash = new SplashWindow();
			splash.Show();
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Failed to show splash screen");
			splash = null;
		}

		try
		{
			// 初始化 SharpSevenZip：提取嵌入式 7z.dll 并设置库路径
			InitializeSharpSevenZip();

			// 先同步初始化 SettingsService 并应用已保存的语言偏好（在创建 MainWindow 之前）
			// 必须在 MainWindow/MainViewModel 创建之前完成，否则 ViewModel 构造函数访问设置会抛 "Object not initialized"
			InitializeSettingsAndLanguageSync();

			MainWindow = Host.Services.GetRequiredService<MainWindow>();
			if (splash is not null)
				MainWindow.ContentRendered += (_, _) => splash.Close();
			MainWindow.Show();
		}
		catch
		{
			splash?.Close();
			throw;
		}
	}

	/// <summary>
	/// 同步初始化 SettingsService 并应用已保存的语言偏好。
	/// 必须在创建依赖 SettingsService 的 ViewModel 之前调用，
	/// 避免 ViewModel 构造函数访问设置属性时抛出 "Object not initialized"。
	/// </summary>
	/// <remarks>
	/// 实现说明：用 <see cref="Task.Run{TResult}(Func{Task{TResult}})"/> 把 InitAsync 放到线程池线程执行，
	/// 再同步等待结果。原因：SettingsService.ReadAsyncFallback 内部的 await 未使用 ConfigureAwait(false)，
	/// 若直接在 UI 线程上 <c>GetAwaiter().GetResult()</c> 阻塞等待，await 续接会尝试回到被阻塞的 UI 线程，导致死锁。
	/// 线程池线程无 SynchronizationContext，await 续接不会回到 UI 线程，从而避免死锁。
	/// 此时尚未显示窗口，短暂阻塞 UI 线程可接受。
	/// </remarks>
	private void InitializeSettingsAndLanguageSync()
	{
		try
		{
			var settingsService = Host.Services.GetRequiredService<SettingsService>();
			var localizationService = Host.Services.GetRequiredService<LocalizationService>();

			// 在线程池线程上运行 InitAsync，避免 UI 线程死锁
			bool initOk = Task.Run(() => settingsService.InitAsync()).GetAwaiter().GetResult();
			if (initOk)
			{
				if (!string.IsNullOrEmpty(settingsService.Language))
				{
					localizationService.SelectedLanguage = settingsService.Language;
					_logger?.LogInformation("Applied saved language preference: {Lang}", settingsService.Language);
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Failed to initialize settings / language preference, using auto-detect");
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

		// 清理测试运行时残留的 hd2mm_* 临时目录
		try
		{
			var tempPath = Path.GetTempPath();
			var dirs = Directory.GetDirectories(tempPath, "hd2mm_*");
			foreach (var dir in dirs)
			{
				try
				{
					Directory.Delete(dir, true);
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "清理临时目录失败: {Dir}", dir);
				}
			}
			if (dirs.Length > 0)
			{
				_logger?.LogInformation("已清理 {Count} 个测试临时目录", dirs.Length);
			}
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "清理测试临时目录时发生异常");
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
		AddApplicationServices(services);
		services.AddCoreBackend();
		services.AddSingleton<LegacyProfileSaveAdapter>();
		services.AddSingleton<global::Helldivers2ModManager.Core.Profiles.ProfileSaveCoordinator>(static provider =>
		{
			var adapter = provider.GetRequiredService<LegacyProfileSaveAdapter>();
			return new global::Helldivers2ModManager.Core.Profiles.ProfileSaveCoordinator(
				adapter.SaveAsync,
				provider.GetRequiredService<ILogger<global::Helldivers2ModManager.Core.Profiles.ProfileSaveCoordinator>>());
		});
		services.AddSingleton<Services.Nexus.INexusModsService>(static _ => new NexusModsServiceAdapter());
	}

	private static void AddApplicationServices(IServiceCollection services)
	{
		services.AddSingleton<Stores.EditModStore>();

		services.AddSingleton<Services.ArmorReuseService>();
		services.AddSingleton<Services.BackgroundTaskService>();
		services.AddSingleton<Services.BisectService>();
		services.AddSingleton<Services.EnabledDataRepository>();
		services.AddSingleton<Services.GpuSkinningService>();
		services.AddSingleton<Services.LocalizationService>();
		services.AddSingleton<Services.ModConflictRepository>();
		services.AddSingleton<Services.ModConflictService>();
		services.AddSingleton<Services.ModelPreviewBackend>();
		services.AddSingleton<Services.ModGroupRepository>();
		services.AddSingleton<Services.ModGroupService>();
		services.AddSingleton<Services.ModHashService>();
		services.AddSingleton<Services.ModService>();
		services.AddSingleton<Services.PatchResourceInspectionService>();
		services.AddSingleton<Services.ProfileSaveCoordinator>();
		services.AddSingleton<Services.ProfileService>();
		services.AddSingleton<Services.RepairDisclaimerService>();
		services.AddSingleton<Services.SettingsService>();
		services.AddSingleton<Services.VersionCheckRepository>();
		services.AddSingleton<Services.VersionCheckService>();

		services.AddTransient<ViewModels.ArmorReusePageViewModel>();
		services.AddTransient<ViewModels.AutoTagPairingPageViewModel>();
		services.AddTransient<ViewModels.BackgroundTasksPageViewModel>();
		services.AddTransient<ViewModels.BackendTestCenterPageViewModel>();
		services.AddTransient<ViewModels.BisectPageViewModel>();
		services.AddTransient<ViewModels.CreatePageViewModel>();
		services.AddTransient<ViewModels.DashboardPageViewModel>();
		services.AddTransient<ViewModels.DeploymentOrderPageViewModel>();
		services.AddTransient<ViewModels.EditPageViewModel>();
		services.AddTransient<ViewModels.HelpPageViewModel>();
		services.AddTransient<ViewModels.MainViewModel>();
		services.AddTransient<ViewModels.ManifestEditPageViewModel>();
		services.AddTransient<ViewModels.ModelPreviewPageViewModel>();
		services.AddTransient<ViewModels.ModGroupSidebarViewModel>();
		services.AddTransient<ViewModels.NexusDownloadPageViewModel>();
		services.AddTransient<ViewModels.PatchResourceViewerPageViewModel>();
		services.AddTransient<Services.SearchFilterService>();
		services.AddTransient<ViewModels.SettingsPageViewModel>();
		services.AddTransient<ViewModels.TagManagementPageViewModel>();
		services.AddTransient<ViewModels.VersionCheckViewModel>();
	}
	
	private void LogUnhandledException(Exception? ex)
	{
		if (_logger is null)
			MessageBox.Show($"An unhandled exception occurred before logging could be initialized!\n\n{ex?.ToString()}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
		else
			_logger?.LogError(ex, "An unhandled exception occured!");
	}
}
