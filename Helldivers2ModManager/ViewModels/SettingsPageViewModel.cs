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
using System.Security.Principal;
using Helldivers2ModManager.Models;
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

	/// <summary>
	/// 是否使用自定义背景图片。
	/// </summary>
	public bool UseCustomBackground
	{
		get => _settingsService.Initialized && _settingsService.BackgroundMode == BackgroundMode.Image;
		set
		{
			OnPropertyChanging();
			_settingsService.BackgroundMode = value ? BackgroundMode.Image : BackgroundMode.Default;
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// 当前背景图片路径（只读展示，由浏览/清除命令修改）。
	/// </summary>
	public string BackgroundImagePath => _settingsService.Initialized ? _settingsService.BackgroundImagePath : string.Empty;

	/// <summary>
	/// 背景图片不透明度（0..1）。
	/// </summary>
	public float BackgroundOpacity
	{
		get => _settingsService.Initialized ? _settingsService.BackgroundOpacity : 0.6f;
		set
		{
			OnPropertyChanging();
			_settingsService.BackgroundOpacity = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<string> SkipList => _settingsService.Initialized ? _settingsService.SkipList : [];

	public ObservableCollection<string> OrganizationalFolderNames => _settingsService.Initialized ? _settingsService.OrganizationalFolderNames : [];

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

			// 如果勾选了符号链接但程序未以管理员身份运行，则弹出提示引导用户
			if (value && !IsRunningAsAdministrator())
			{
				WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
				{
					Title = _localizationService["MessageBox.Info"],
					Message = _localizationService["SettingsPage.SymbolicLinkAdminMsg"],
					Confirm = static () => System.Windows.Application.Current.Shutdown()
				});
			}
		}
	}

	/// <summary>
	/// 检测当前程序是否以管理员身份运行
	/// </summary>
	private static bool IsRunningAsAdministrator()
	{
		using var identity = WindowsIdentity.GetCurrent();
		var principal = new WindowsPrincipal(identity);
		return principal.IsInRole(WindowsBuiltInRole.Administrator);
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

	public bool DeployBottomToTop
	{
		get => _settingsService.Initialized ? _settingsService.DeployBottomToTop : false;
		set
		{
			OnPropertyChanging();
			_settingsService.DeployBottomToTop = value;
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// 是否启用自定义部署顺序
	/// </summary>
	public bool UseDeploymentOrder
	{
		get => _settingsService.Initialized && _settingsService.UseDeploymentOrder;
		set
		{
			OnPropertyChanging();
			_settingsService.UseDeploymentOrder = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(IsCustomOrderEnabled));
		}
	}

	/// <summary>
	/// 启用自定义部署顺序时显示编辑区
	/// </summary>
	public bool IsCustomOrderEnabled => _settingsService.Initialized && _settingsService.UseDeploymentOrder;

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

	public bool EnableBatchRepair
	{
		get => _settingsService.Initialized ? _settingsService.EnableBatchRepair : false;
		set
		{
			OnPropertyChanging();
			_settingsService.EnableBatchRepair = value;
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

	public int MaxLogFiles
	{
		get => _settingsService.Initialized ? _settingsService.MaxLogFiles : 20;
		set
		{
			OnPropertyChanging();
			_settingsService.MaxLogFiles = value;
			OnPropertyChanged();
		}
	}

	public bool ShowSeparator
	{
		get => _settingsService.Initialized ? _settingsService.ShowSeparator : true;
		set
		{
			OnPropertyChanging();
			_settingsService.ShowSeparator = value;
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

	/// <summary>
	/// 当前选中的选项卡索引
	/// 0: 路径, 1: 部署, 2: 模组, 3: 日志, 4: 连接, 5: 工具, 6: 主页, 7: 外观
	/// </summary>
	[ObservableProperty]
	private int _selectedTabIndex;

	/// <summary>
	/// 当前选中的语言代码。
	/// 空字符串表示自动检测，非空值表示手动指定的语言。
	/// </summary>
	public string SelectedLanguageCode
	{
		get => _selectedLanguageCode;
		set
		{
			if (_selectedLanguageCode == value)
				return;
			OnPropertyChanging();
			_selectedLanguageCode = value;
			OnPropertyChanged();

			// 应用语言切换
			_localizationService.SelectedLanguage = value;

			// 同步到设置（只在保存时持久化，但先缓存）
			if (_settingsService.Initialized)
			{
				_settingsService.Language = value;
			}
		}
	}
	private string _selectedLanguageCode = string.Empty;

	/// <summary>
	/// 可用的语言列表（来自 LocalizationService）。
	/// 第一个选项为"自动检测"，值为空字符串。
	/// </summary>
	public ObservableCollection<LanguageItem> AvailableLanguages => _localizationService.AvailableLanguages;

	private readonly ILogger<SettingsPageViewModel> _logger;
	private readonly NavigationStore _navStore;
	private readonly SettingsService _settingsService;
	private readonly INexusModsService _nexusModsService;
	private readonly ModHashService _modHashService;
	private readonly ModService _modService;
	private readonly LocalizationService _localizationService;
	[ObservableProperty]
	private int _selectedSkip = -1;
	[ObservableProperty]
	private int _selectedOrgFolder = -1;

	public SettingsPageViewModel(ILogger<SettingsPageViewModel> logger, NavigationStore navStore, SettingsService settingsService, INexusModsService nexusModsService, ModHashService modHashService, ModService modService, LocalizationService localizationService)
	{
		_logger = logger;
		_navStore = navStore;
		_settingsService = settingsService;
		_nexusModsService = nexusModsService;
		_modHashService = modHashService;
		_modService = modService;
		_localizationService = localizationService;

		SkipList.CollectionChanged += SkipList_CollectionChanged;
		OrganizationalFolderNames.CollectionChanged += OrgFolderNames_CollectionChanged;

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
		OrganizationalFolderNames.CollectionChanged -= OrgFolderNames_CollectionChanged;
		MessageBox.Registered -= OnMessageBoxRegistered;
	}

	private bool ValidateGameDir(DirectoryInfo dir, [NotNullWhen(false)] out string? error)
	{
		if (!dir.Exists)
		{
			error = _localizationService["SettingsPage.ValidateGameDirNotExist"];
			return false;
		}

		if (dir is not DirectoryInfo { Name: "Helldivers 2" })
		{
			error = _localizationService["SettingsPage.ValidateGameDirInvalid"];
			return false;
		}

		var subDirs = dir.EnumerateDirectories();
		if (!subDirs.Any(static d => d.Name == "data"))
		{
			error = _localizationService["SettingsPage.ValidateGameDirNoData"];
			return false;
		}
		if (!subDirs.Any(static d => d.Name == "tools"))
		{
			error = _localizationService["SettingsPage.ValidateGameDirNoTools"];
			return false;
		}
		if (subDirs.FirstOrDefault(static d => d.Name == "bin") is not DirectoryInfo binDir)
		{
			error = _localizationService["SettingsPage.ValidateGameDirNoBin"];
			return false;
		}
		if (!binDir.GetFiles("helldivers2.exe").Any())
		{
			error = _localizationService["SettingsPage.ValidateGameDirNoExe"];
			return false;
		}

		error = null;
		return true;
	}

	protected override void OnPropertyChanged(PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SelectedSkip))
			RemoveSkipCommand.NotifyCanExecuteChanged();
		if (e.PropertyName == nameof(SelectedOrgFolder))
			RemoveOrgFolderCommand.NotifyCanExecuteChanged();

		base.OnPropertyChanged(e);
	}

	private bool ValidateSettings()
	{
		if (string.IsNullOrEmpty(GameDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = _localizationService["SettingsPage.ValidateGameDirEmpty"]
            });
			return false;
		}

		if (string.IsNullOrEmpty(StorageDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = _localizationService["SettingsPage.ValidateStorageDirEmpty"]
            });
			return false;
		}

		if (string.IsNullOrEmpty(TempDir))
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = _localizationService["SettingsPage.ValidateTempDirEmpty"]
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
			Title = _localizationService["SettingsPage.LoadingSettings"],
			Message = _localizationService["SettingsPage.PleaseWait"],
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
				Title = _localizationService["SettingsPage.LoadSettingsFailed"],
				Message = _localizationService["SettingsPage.ResetConfirm"],
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

		// 应用已保存的语言设置到本地化服务（覆盖构造时的自动检测）
		if (!string.IsNullOrEmpty(_settingsService.Language))
		{
			_localizationService.SelectedLanguage = _settingsService.Language;
		}
		_selectedLanguageCode = _settingsService.Language;

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
		OnPropertyChanged(nameof(OrganizationalFolderNames));
		OnPropertyChanged(nameof(CaseSensitiveSearch));
		OnPropertyChanged(nameof(UseSymbolicLinks));
		OnPropertyChanged(nameof(EnableSorting));
		OnPropertyChanged(nameof(DeployBottomToTop));
		OnPropertyChanged(nameof(UseDeploymentOrder));
		OnPropertyChanged(nameof(IsCustomOrderEnabled));
		OnPropertyChanged(nameof(DeleteToRecycleBin));
		OnPropertyChanged(nameof(AutoRemoveMissingMods));
		OnPropertyChanged(nameof(AutoCheckVersionOnStartup));
		OnPropertyChanged(nameof(EnableBatchRepair));
		OnPropertyChanged(nameof(AutoCleanLogs));
		OnPropertyChanged(nameof(ShowSeparator));
		OnPropertyChanged(nameof(MaxLogFiles));
		OnPropertyChanged(nameof(NexusApiKey));
		OnPropertyChanged(nameof(SelectedLanguageCode));
		OnPropertyChanged(nameof(AvailableLanguages));
	}

	private void SkipList_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		RemoveSkipCommand.NotifyCanExecuteChanged();
	}

	private void OrgFolderNames_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		RemoveOrgFolderCommand.NotifyCanExecuteChanged();
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
				Message = _localizationService["SettingsPage.SettingsValid"],
			});
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = _localizationService["SettingsPage.SavingSettings"],
			Message = _localizationService["SettingsPage.PleaseWait"]
        });
		try
		{
			await _settingsService.SaveAsync();

			// 保存设置后立即更新运行时日志级别过滤
			App.Current.LogLevel = _settingsService.LogLevel;

			// 保存后执行日志清理
			_settingsService.CleanExcessLogs();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to save settings");
			WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
			{
				Message = _localizationService["SettingsPage.SaveFailed"].Replace("{message}", ex.Message),
			});
			return;
		}
		WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

		_navStore.Navigate<DashboardPageViewModel>();
	}

	[RelayCommand]
	async Task Cancel()
	{
		_logger.LogInformation("User cancelled settings changes");
		
		await _settingsService.ReloadAsync();
		
		if (!string.IsNullOrEmpty(_settingsService.Language))
		{
			_localizationService.SelectedLanguage = _settingsService.Language;
		}
		
		_navStore.Navigate<DashboardPageViewModel>();
	}

	/// <summary>
	/// 切换设置选项卡（XAML CommandParameter 传递的是字符串，需要手动解析为 int）
	/// </summary>
	[RelayCommand]
	void SetTab(object parameter)
	{
		if (parameter is int index)
		{
			SelectedTabIndex = index;
		}
		else if (parameter is string str && int.TryParse(str, out var parsedIndex))
		{
			SelectedTabIndex = parsedIndex;
		}
	}

	[RelayCommand]
	void Reset()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = _localizationService["SettingsPage.ResetTitle"],
			Message = _localizationService["SettingsPage.ResetConfirmMsg"],
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
			Title = _localizationService["SettingsPage.BrowseGameDialog"]
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
	void BrowseBackgroundImage()
	{
		var dialog = new OpenFileDialog
		{
			Multiselect = false,
			Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*",
			Title = _localizationService["SettingsPage.BrowseBackgroundImageDialog"]
		};

		if (dialog.ShowDialog() ?? false)
		{
			_settingsService.BackgroundImagePath = dialog.FileName;
			_settingsService.BackgroundMode = BackgroundMode.Image;
			OnPropertyChanged(nameof(BackgroundImagePath));
			OnPropertyChanged(nameof(UseCustomBackground));
		}
	}

	[RelayCommand]
	void ClearBackgroundImage()
	{
		_settingsService.BackgroundImagePath = string.Empty;
		_settingsService.BackgroundMode = BackgroundMode.Default;
		OnPropertyChanged(nameof(BackgroundImagePath));
		OnPropertyChanged(nameof(UseCustomBackground));
	}

	[RelayCommand]
	void BrowseStorage()
	{
		var dialog = new OpenFolderDialog
		{
			Multiselect = false,
			ValidateNames = true,
			Title = _localizationService["SettingsPage.BrowseStorageDialog"]
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
			Title = _localizationService["SettingsPage.BrowseTempDialog"]
        };

		if (dialog.ShowDialog() ?? false)
			TempDir = dialog.FolderName;
	}

	[RelayCommand]
	void HardPurge()
	{
		_logger.LogInformation("Hard purging patch files");
		
		try
		{
			var path = Path.Combine(_settingsService.StorageDirectory, "installed.txt");
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "删除 installed.txt 失败");
		}

		try
		{
			var dataDir = new DirectoryInfo(Path.Combine(_settingsService.GameDirectory, "data"));

			var files = dataDir.EnumerateFiles("*.patch_*").ToArray();
			_logger.LogDebug("Found {} patch files", files.Length);

			foreach (var file in files)
			{
				try
				{
					_logger.LogTrace("Deleting \"{}\"", file.Name);
					file.Delete();
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "删除补丁文件失败: {File}", file.Name);
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "枚举补丁文件失败");
		}

		_logger.LogInformation("Hard purge complete");
	}

	[RelayCommand]
	void AddSkip()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
		{
			Title = _localizationService["SettingsPage.AddSkipTitle"],
			Message = _localizationService["SettingsPage.AddSkipMsg"],
			MaxLength = 16,
			Confirm = (str) =>
			{
				if (str.Length == 16)
					SkipList.Add(str);
				else
					WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
					{
						Message = _localizationService["SettingsPage.AddSkipValidation"]
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
	void AddOrgFolder()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
		{
			Title = _localizationService["SettingsPage.AddOrgFolderTitle"],
			Message = _localizationService["SettingsPage.AddOrgFolderMsg"],
			MaxLength = 100,
			Confirm = (str) =>
			{
				var name = str.Trim();
				if (string.IsNullOrWhiteSpace(name))
					return;

				// 检查是否已存在（大小写不敏感）
				if (OrganizationalFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
					{
						Message = _localizationService["SettingsPage.AddOrgFolderExists"].Replace("{name}", name)
					});
					return;
				}

				OrganizationalFolderNames.Add(name);
			}
		});
	}

	private static readonly HashSet<string> _defaultOrgFolderNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"Models", "Model"
	};

	bool CanRemoveOrgFolder()
	{
		if (SelectedOrgFolder == -1)
			return false;
		if (SelectedOrgFolder >= OrganizationalFolderNames.Count)
			return false;
		var name = OrganizationalFolderNames[SelectedOrgFolder];
		return !_defaultOrgFolderNames.Contains(name);
	}

	[RelayCommand(CanExecute = nameof(CanRemoveOrgFolder))]
	void RemoveOrgFolder()
	{
		OrganizationalFolderNames.RemoveAt(SelectedOrgFolder);
	}

	[RelayCommand]
	async Task ValidateNexusApiKey()
	{
		if (string.IsNullOrWhiteSpace(NexusApiKey))
		{
			NexusApiKeyValidationResult = _localizationService["SettingsPage.ApiKeyEmpty"];
			return;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = _localizationService["SettingsPage.ValidatingApiKeyTitle"],
			Message = _localizationService["SettingsPage.ValidatingApiKeyMsg"]
		});

		try
		{
			_nexusModsService.Init(NexusApiKey);
			await _nexusModsService.GetTrendingModsAsync("helldivers2");
			NexusApiKeyValidationResult = _localizationService["SettingsPage.ApiKeyValid"];
			_logger.LogInformation("Nexus API Key validated successfully");
		}
		catch (Exception ex)
		{
			NexusApiKeyValidationResult = _localizationService["SettingsPage.ApiKeyFailed"].Replace("{message}", ex.Message);
			_logger.LogError(ex, "Failed to validate Nexus API Key");
		}
		finally
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
		}
	}

	/// <summary>
	/// 强制重新计算所有模组的文件哈希值。
	/// 适用于：用户怀疑哈希缓存数据异常、手动修改过模组文件后需要刷新等情况。
	/// 后台执行，完成后底部状态栏会显示结果摘要。
	/// </summary>
	[RelayCommand]
	Task RecomputeAllHashes()
	{
		var modCount = _modService.Mods.Count;
		if (modCount == 0)
		{
			WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
			{
				Message = _localizationService["SettingsPage.NoModsForHash"]
			});
			return Task.CompletedTask;
		}

		WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
		{
			Title = _localizationService["Common.WarningPrefix"],
			Message = _localizationService["SettingsPage.RecomputeHashMsg"].Replace("{count}", modCount.ToString()),
			Confirm = () =>
			{
				_logger.LogInformation("User requested full hash recomputation for {Count} mods", modCount);
				_ = _modHashService.ForceRecomputeAllAsync(_modService.Mods);
				WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage
				{
					Message = _localizationService["SettingsPage.RecomputeHashStarted"]
				});
			}
		});

		return Task.CompletedTask;
	}

	[RelayCommand]
	async Task DetectGame()
	{
		WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
		{
			Title = _localizationService["SettingsPage.DetectingGame"],
			Message = _localizationService["SettingsPage.PleaseWait"]
		});

		var (result, path) = await Task.Run<(bool, string?)>(() =>
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
				Message = _localizationService["SettingsPage.DetectGameFailed"]
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
