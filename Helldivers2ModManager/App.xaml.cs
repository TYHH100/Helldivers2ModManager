// Ignore Spelling: App

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
	public static readonly Version Version = new(1, 4, 0, 2);

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

