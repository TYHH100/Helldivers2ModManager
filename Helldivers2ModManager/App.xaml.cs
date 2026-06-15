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
	public static readonly Version Version = new(1, 4, 1, 0);

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

		MainWindow = Host.Services.GetRequiredService<MainWindow>();
		MainWindow.Show();

		Task.Run(async () =>
		{
			await Task.Delay(1000);
			await Dispatcher.InvokeAsync(() =>
			{
				try
				{
					var browserExtensionService = Host.Services.GetRequiredService<BrowserExtensionService>();
					browserExtensionService.Start();
					_logger?.LogInformation("Browser extension service started successfully");
				}
				catch (Exception ex)
				{
					_logger?.LogWarning(ex, "Failed to start browser extension service");
				}
			});
		});
	}

	protected override void OnExit(ExitEventArgs e)
	{
		base.OnExit(e);

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

