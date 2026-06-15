using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.Services.Nexus;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class SettingsPageViewModel : PageViewModelBase
{
	public override string Title => "Settings";

	public string GameDir
	{
		get => _settingsService.Initialized ? _settingsService.GameDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.GameDirectory = value;
			OnPropertyChanged();
		}
	}

	public string TempDir
	{
		get => _settingsService.Initialized ? _settingsService.TempDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.TempDirectory = value;
			OnPropertyChanged();
		}
	}

	public string StorageDir
	{
		get => _settingsService.Initialized ? _settingsService.StorageDirectory : string.Empty;
		set
		{
			OnPropertyChanging();
			_settingsService.StorageDirectory = value;
			OnPropertyChanged();
		}
	}

	public LogLevel LogLevel
	{
		get => _settingsService.Initialized ? _settingsService.LogLevel : LogLevel.Warning;
		set
		{
			OnPropertyChanging();
			_settingsService.LogLevel = value;
			OnPropertyChanged();
		}
	}

	public float Opacity
	{
		get => _settingsService.Initialized ? _settingsService.Opacity : 0.8f;
		set
		{
			OnPropertyChanging();
			_settingsService.Opacity = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<string> SkipList => _settingsService.Initialized ? _settingsService.SkipList : [];

	public bool CaseSensitiveSearch
	{
		get => _settingsService.Initialized ? _settingsService.CaseSensitiveSearch : false;
		set
		{
			OnPropertyChanging();
			_settingsService.CaseSensitiveSearch = value;
			OnPropertyChanged();
		}
	}

	public bool UseSymbolicLinks
	{
		get => _settingsService.Initialized ? _settingsService.UseSymbolicLinks : false;
		set
		{
			OnPropertyChanging();
			_settingsService.UseSymbolicLinks = value;
			OnPropertyChanged();
		}
	}

	public bool EnableSorting
	{
		get => _settingsService.Initialized ? _settingsService.EnableSorting : false;
		set
		{
			OnPropertyChanging();
			_settingsService.EnableSorting = value;
			OnPropertyChanged();
		}
	}

	public bool DeleteToRecycleBin
	{
		get => _settingsService.Initialized ? _settingsService.DeleteToRecycleBin : true;
		set
		{
			OnPropertyChanging();
			_settingsService.DeleteToRecycleBin = value;
			OnPropertyChanged();
		}
	}

	public bool AutoRemoveMissingMods
	{
		get => _settingsService.Initialized ? _settingsService.AutoRemoveMissingMods : false;
		set
		{
			OnPropertyChanging();
			_settingsService.AutoRemoveMissingMods = value;
			OnPropertyChanged();
		}
	}

	public bool AutoCheckVersionOnStartup
	{
		get => _settingsService.Initialized ? _settingsService.AutoCheckVersionOnStartup : false;
		set
		{
			OnPropertyChanging();
			_settingsService.AutoCheckVersionOnStartup = value;
			OnPropertyChanged();
		}
	}

	public bool AutoCleanLogs
	{
		get => _settingsService.Initialized ? _settingsService.AutoCleanLogs : true;
		set
		{
			OnPropertyChanging();
			_settingsService.AutoCleanLogs = value;
			OnPropertyChanged();
		}
	}

	public int LogRetentionDays
	{
		get => _settingsService.Initialized ? _settingsService.LogRetentionDays : 7;
		set
		{
			OnPropertyChanging();
			_settingsService.LogRetentionDays = value;
			OnPropertyChanged();
		}
	}

	public string ExtensionHost
	{
		get => _settingsService.Initialized ? _settingsService.ExtensionHost : "localhost";
		set
		{
			OnPropertyChanging();
			_settingsService.ExtensionHost = value;
			OnPropertyChanged();
		}
	}

	public int ExtensionPort
	{
		get => _settingsService.Initialized ? _settingsService.ExtensionPort : 7456;
		set
		{
			OnPropertyChanging();
			_settingsService.ExtensionPort = value;
			OnPropertyChanged();
		}
	}

	public string? NexusApiKey
	{
		get => _settingsService.Initialized ? _settingsService.NexusApiKey : null;
		set
		{
			OnPropertyChanging();
			_settingsService.NexusApiKey = value;
			OnPropertyChanged();
		}
	}

	[ObservableProperty]
	private string? _nexusApiKeyValidationResult;

	private readonly ILogger<SettingsPageViewModel> _logger;
	private readonly NavigationStore _navStore;
	private readonly SettingsService _settingsService;
	private readonly INexusModsService _nexusModsService;
	[ObservableProperty]
	private int _selectedSkip = -1;

	public SettingsPageViewModel(ILogger<SettingsPageViewModel> logger, NavigationStore navStore, SettingsService settingsService, INexusModsService nexusModsService)
	{
		_logger = logger;
		_navStore = navStore;
		_settingsService = settingsService;
		_nexusModsService = nexusModsService;

		SkipList.CollectionChanged += SkipList_CollectionChanged;

		if (MessageBox.IsRegistered)
			_ = Init();
		else
			MessageBox.Registered += OnMessageBoxRegistered;
	}

	private void OnMessageBoxRegistered(object? sender, EventArgs e)
	{
		_ = Init();
	}

	protected override void OnDispose()
	{
		SkipList.CollectionChanged -= SkipList_CollectionChanged;
		MessageBox.Registered -= OnMessageBoxRegistered;
	}

	private static bool ValidateGameDir(DirectoryInfo dir, [NotNullWhen(false)] out string? error)
	{
		if (!dir.Exists)
		{
			error = "选择的Helldivers 2文件夹不存在!";
			return false;
		}

		if (dir is not DirectoryInfo { Name: "Helldivers 2" })
		{
			error = "选择的Helldivers 2文件夹并不在有效目录中!";
			return false;
		}

		var subDirs = dir.EnumerateDirectories();
		if (!subDirs.Any(static d => d.Name == "data"))
		{
			error = "选择的Helldivers 2根目录中没有名为 \"data\" 文件夹!";
			return false;
		}
		if (!subDirs.Any(static d => d.Name == "tools"))
		{
			error = "选择的Helldivers 2根目录中没有名为 \"tools\" 文件夹!";
			return false;
		}
		if (subDirs.FirstOrDefault(static d => d.Name == "bin") is not DirectoryInfo binDir)
		{
			error = "选择的Helldivers 2根目录中没有名为 \"bin\" 文件夹!";
			return false;
		}
		if (!binDir.GetFiles("helldivers2.exe").Any())
		{
			error = "选定的Helldivers 2文件路径中,在 \"bin\" 文件夹中没有 \"helldivers2.exe\" 文件!";
			return false;
		}

		error = null;
		return true;
	}

	protected override void OnPropertyChanged(PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SelectedSkip))
			RemoveSkipCommand.NotifyCanExecuteChanged();

		base.OnPropertyChanged(e);
	}

	private bool ValidateSettings()
	{
		if (string.IsNullOrEmpty(GameDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "游戏目录不能为空!"
            });
			return false;
		}

		if (string.IsNullOrEmpty(StorageDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "存储目录不能为空!"
            });
			return false;
		}

		if (string.IsNullOrEmpty(TempDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "临时目录不能为空!"
            });
			return false;
		}

		return true;
	}

	private async Task Init()
	{
		_logger.LogInformation("Loading settings...");
		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "加载设置中",
			Message = "请民主官耐心等待.",
		});
		try
		{
			if (!await _settingsService.InitAsync())
				_settingsService.InitDefault();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Loading settings failed");
			WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
			{
				Title = "加载设置失败!",
				Message = "是否需要重置设置?",
				Confirm = () =>
				{
					_settingsService.InitDefault();
					Update();
				},
			});
			return;
		}
		_logger.LogInformation("Settings loaded successfully");
		WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
		Update();
	}

	private void Update()
	{
		OnPropertyChanged(nameof(GameDir));
		OnPropertyChanged(nameof(TempDir));
		OnPropertyChanged(nameof(StorageDir));
		OnPropertyChanged(nameof(LogLevel));
		OnPropertyChanged(nameof(Opacity));
		OnPropertyChanged(nameof(SkipList));
		OnPropertyChanged(nameof(CaseSensitiveSearch));
		OnPropertyChanged(nameof(UseSymbolicLinks));
		OnPropertyChanged(nameof(EnableSorting));
		OnPropertyChanged(nameof(DeleteToRecycleBin));
		OnPropertyChanged(nameof(AutoRemoveMissingMods));
		OnPropertyChanged(nameof(AutoCheckVersionOnStartup));
		OnPropertyChanged(nameof(AutoCleanLogs));
		OnPropertyChanged(nameof(LogRetentionDays));
		OnPropertyChanged(nameof(ExtensionHost));
		OnPropertyChanged(nameof(ExtensionPort));
		OnPropertyChanged(nameof(NexusApiKey));
	}

	private void SkipList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		RemoveSkipCommand.NotifyCanExecuteChanged();
	}

	[RelayCommand]
	async Task Ok()
	{
		if (!ValidateSettings())
			return;

		if (!_settingsService.Validate())
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = "无效设置!",
			});
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "保存设置中",
			Message = "请民主官耐心等待."
        });
		try
		{
			await _settingsService.SaveAsync();

			// 保存设置后立即更新运行时日志级别过滤
			App.Current.LogLevel = _settingsService.LogLevel;

			// 保存后执行日志清理
			_settingsService.CleanOldLogs();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to save settings");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = $"设置保存失败!\n\n{ex.Message}",
			});
			return;
		}
		WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	void Reset()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = "重置?",
			Message = "您真的要重置设置?",
			Confirm = () =>
			{
				_settingsService.Reset();
				Update();
			}
		});
	}

	[RelayCommand]
	void BrowseGame()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			Title = "请选择您的Helldivers 2文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
		{
			var newDir = new DirectoryInfo(dialog.FolderName);

			if (newDir.Parent is DirectoryInfo { Name: "Helldivers 2" })
			{
				newDir = newDir.Parent;
			}

			if (ValidateGameDir(newDir, out var error))
				GameDir = newDir.FullName;
			else
				WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
				{
					Message = error
				});
		}
	}

	[RelayCommand]
	void BrowseStorage()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			ValidateNames = true,
			Title = "选择您想要模组管理器|存放模组的文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
			StorageDir = dialog.FolderName;
	}

	[RelayCommand]
	void BrowseTemp()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			ValidateNames = true,
			Title = "选择您想要模组管理器|存放临时文件的文件夹..."
        };

		if (dialog.ShowDialog() ?? false)
			TempDir = dialog.FolderName;
	}

	[RelayCommand]
	void HardPurge()
	{
		_logger.LogInformation("Hard purging patch files");
		
		var path = Path.Combine(_settingsService.StorageDirectory, "installed.txt");
		if (File.Exists(path))
			File.Delete(path);

		var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));
		
		var files = dataDir.EnumerateFiles("*.patch_*").ToArray();
		_logger.LogDebug("Found {} patch files", files.Length);

		foreach (var file in files)
		{
			_logger.LogTrace("Deleting \"{}\"", file.Name);
			file.Delete();
		}

		_logger.LogInformation("Hard purge complete");
	}

	[RelayCommand]
	void AddSkip()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
		{
			Title = "文件名?",
			Message = "Please enter the 16 character name of an archive file you want to skip patch 0 for.",
			MaxLength = 16,
			Confirm = (str) =>
			{
				if (str.Length == 16)
					SkipList.Add(str);
				else
					WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
					{
						Message = "Mod文件名的长度只能为 16 字符串."
                    });
			}
		});
	}

	bool CanRemoveSkip()
	{
		return SelectedSkip != -1;
	}

	[RelayCommand(CanExecute = nameof(CanRemoveSkip))]
	void RemoveSkip()
	{
		SkipList.RemoveAt(SelectedSkip);
	}

	[RelayCommand]
	async Task ValidateNexusApiKey()
	{
		if (string.IsNullOrWhiteSpace(NexusApiKey))
		{
			NexusApiKeyValidationResult = "API Key 不能为空";
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "验证 API Key",
			Message = "正在验证 Nexus Mods API Key..."
		});

		try
		{
			_nexusModsService.Init(NexusApiKey);
			await _nexusModsService.GetTrendingModsAsync("helldivers2");
			NexusApiKeyValidationResult = "API Key 验证成功!";
			_logger.LogInformation("Nexus API Key validated successfully");
		}
		catch (Exception ex)
		{
			NexusApiKeyValidationResult = $"验证失败: {ex.Message}";
			_logger.LogError(ex, "Failed to validate Nexus API Key");
		}
		finally
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
		}
	}

	[RelayCommand]
	async Task DetectGame()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = "查找游戏",
			Message = "请民主官耐心等待."
		});

		var (result, path) = await Task.Run<(bool, string?)>(static () =>
		{
			var steamPath = GetSteamInstallPath();
			if (!string.IsNullOrEmpty(steamPath))
			{
				var steamLibraries = GetSteamLibraryFolders(steamPath);
				
				foreach (var library in steamLibraries)
				{
					var gamePath = Path.Combine(library, "steamapps", "common", "Helldivers 2");
					if (ValidateGameDir(new DirectoryInfo(gamePath), out _))
						return (true, gamePath);
				}
			}

			foreach(var drive in Environment.GetLogicalDrives())
			{
				string path;
				if (drive == "C:\\")
				{
					path = Path.Combine(drive, "Program Files (x86)", "Steam", "steamapps", "common", "Helldivers 2");
					if (ValidateGameDir(new DirectoryInfo(path), out _))
						return (true, path);
				}

				path = Path.Combine(drive, "Steam", "steamapps", "common", "Helldivers 2");
				if (ValidateGameDir(new DirectoryInfo(path), out _))
					return (true, path);

				path = Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Helldivers 2");
				if (ValidateGameDir(new DirectoryInfo(path), out _))
					return (true, path);
			}

			return (false, null);
		});

        if (result && path != null)
        {
            GameDir = path;
			WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
        else
			WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
			{
				Message = "无法自动找到 Helldivers 2 游戏,请手动设置."
			});
	}

	private static string? GetSteamInstallPath()
	{
		try
		{
			// 从当前用户注册表查找 Steam
			using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
			{
				if (key != null)
				{
					var steamPath = key.GetValue("SteamPath") as string;
					if (!string.IsNullOrEmpty(steamPath) && Directory.Exists(steamPath))
						return steamPath;
				}
			}
			
			// 从本地机器注册表查找（64位）
			using (var key = Registry.LocalMachine.OpenSubKey(@"Software\Valve\Steam"))
			{
				if (key != null)
				{
					var installPath = key.GetValue("InstallPath") as string;
					if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
						return installPath;
				}
			}
			
			// 从本地机器注册表查找（32位）
			using (var key = Registry.LocalMachine.OpenSubKey(@"Software\Wow6432Node\Valve\Steam"))
			{
				if (key != null)
				{
					var installPath = key.GetValue("InstallPath") as string;
					if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
						return installPath;
				}
			}
		}
		catch
		{
			// 注册表访问出错，忽略
		}
		
		return null;
	}

	private static List<string> GetSteamLibraryFolders(string steamPath)
	{
		var libraries = new List<string> { steamPath }; // Steam 安装目录本身也是一个库
		
		try
		{
			var libraryFoldersPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
			if (!File.Exists(libraryFoldersPath))
				return libraries;
			
			var content = File.ReadAllText(libraryFoldersPath);
			
			// 简单解析 libraryfolders.vdf，查找 "path" 键对应的目录
			// libraryfolders.vdf 的格式类似：
			// "libraryfolders"
			// {
			//   "0"
			//   {
			//     "path"		"C:\\Program Files (x86)\\Steam"
			//     ...
			//   }
			//   "1"
			//   {
			//     "path"		"D:\\SteamLibrary"
			//     ...
			//   }
			// }
			
			var pattern = @"""path""\s*""([^""]+)""";
			var matches = System.Text.RegularExpressions.Regex.Matches(content, pattern);
			
			foreach (System.Text.RegularExpressions.Match match in matches)
			{
				var path = match.Groups[1].Value.Replace(@"\\", @"\");
				if (!libraries.Contains(path) && Directory.Exists(path))
					libraries.Add(path);
			}
		}
		catch
		{
			// 解析出错，只返回默认的 Steam 安装目录
		}
		
		return libraries;
	}
}
