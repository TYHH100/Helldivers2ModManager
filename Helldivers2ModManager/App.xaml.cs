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

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		// 初始化 SharpSevenZip：提取嵌入式 7z.dll 并设置库路径
		InitializeSharpSevenZip();

		// 尽早加载设置并应用已保存的语言偏好（在显示主窗口之前）
		InitializeLanguagePreference();

		MainWindow = Host.Services.GetRequiredService<MainWindow>();
		MainWindow.Show();

	}

	/// <summary>
	/// 在启动时尽早加载设置并应用已保存的语言偏好，
	/// 避免使用自动检测（系统语言）覆盖用户手动指定的语言。
	/// 异步执行以避免阻塞 UI 线程导致死锁。
	/// </summary>
	private void InitializeLanguagePreference()
	{
		_ = InitializeLanguageAsync();
	}

	private async Task InitializeLanguageAsync()
	{
		try
		{
			var settingsService = Host.Services.GetRequiredService<SettingsService>();
			var localizationService = Host.Services.GetRequiredService<LocalizationService>();

			if (await settingsService.InitAsync().ConfigureAwait(false))
			{
				if (!string.IsNullOrEmpty(settingsService.Language))
				{
					await Dispatcher.InvokeAsync(() =>
					{
						localizationService.SelectedLanguage = settingsService.Language;
						_logger?.LogInformation("Applied saved language preference: {Lang}", settingsService.Language);
					});
				}
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
		if (_logger is null)
			MessageBox.Show($"An unhandled exception occurred before logging could be initialized!\n\n{ex?.ToString()}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
		else
			_logger?.LogError(ex, "An unhandled exception occured!");
	}
}
