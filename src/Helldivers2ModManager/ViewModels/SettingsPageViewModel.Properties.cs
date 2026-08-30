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
using Helldivers2ModManager.Services.Infrastructure;

namespace Helldivers2ModManager.ViewModels;

internal sealed partial class SettingsPageViewModel
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

	/// <summary>
	/// 卡片不透明度（0.3..1.0），控制主页等页面卡片的半透明程度。
	/// </summary>
	public float CardOpacity
	{
		get => _settingsService.Initialized ? _settingsService.CardOpacity : 0.7f;
		set
		{
			OnPropertyChanging();
			_settingsService.CardOpacity = value;
			OnPropertyChanged();
			// 实时预览：滑块拖动时立即更新全局卡片半透明
			MainViewModel.ApplyCardOpacity(value);
		}
	}

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

	/// <summary>
	/// 搜索框模糊搜索（拼音/首字母/子序列匹配）
	/// </summary>
	public bool EnableFuzzySearch
	{
		get => _settingsService.Initialized ? _settingsService.EnableFuzzySearch : true;
		set
		{
			OnPropertyChanging();
			_settingsService.EnableFuzzySearch = value;
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
		get => _settingsService.Initialized ? _settingsService.ShowSeparator : false;
		set
		{
			OnPropertyChanging();
			_settingsService.ShowSeparator = value;
			OnPropertyChanged();
		}
	}

	public bool EnableAutoTagging
	{
		get => _settingsService.Initialized ? _settingsService.EnableAutoTagging : false;
		set
		{
			OnPropertyChanging();
			_settingsService.EnableAutoTagging = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(AutoTagPairingButtonVisible));
		}
	}

	public bool AutoTagCreateMissingTags
	{
		get => _settingsService.Initialized ? _settingsService.AutoTagCreateMissingTags : false;
		set
		{
			OnPropertyChanging();
			_settingsService.AutoTagCreateMissingTags = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(AutoTagPairingButtonVisible));
		}
	}

	/// <summary>
	/// 仅当「启用自动打标签」开启且「自动创建缺失标签」关闭时，显示手动配对入口。
	/// </summary>
	public bool AutoTagPairingButtonVisible =>
		_settingsService.Initialized && _settingsService.EnableAutoTagging && !_settingsService.AutoTagCreateMissingTags;

	[RelayCommand]
	void OpenAutoTagPairing()
	{
		_navStore.Navigate<AutoTagPairingPageViewModel>();
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
	/// 0: 路径, 1: 部署, 2: 模组, 3: 日志, 4: 工具, 5: 主页, 6: 外观
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
}
