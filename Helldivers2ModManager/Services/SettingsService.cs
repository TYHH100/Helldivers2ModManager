using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

internal sealed class SettingsService
{
	public const float OpacityMax = 1.0f;
	
	public const float OpacityMin = 0.4f;
	
	/// <summary>
	/// 设置保存后触发（用于界面刷新依赖设置的显示）。
	/// </summary>
	public event EventHandler? SettingsChanged;

	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_separators))]
	[JsonIgnore]
	public bool Initialized { get; private set; }
	
	[JsonIgnore]
	public bool IsReadonly { get; private set; } = true;

	public string GameDirectory
	{
		get
		{
			GuardInitialized();
			return _gameDirectory;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_gameDirectory = value;
		}
	}

	public string StorageDirectory
	{
		get
		{
			GuardInitialized();
			return _storageDirectory;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_storageDirectory = value;
		}
	}

	public string TempDirectory
	{
		get
		{
			GuardInitialized();
			return _tempDirectory;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_tempDirectory = value;
		}
	}

	public LogLevel LogLevel
	{
		get
		{
			GuardInitialized();
			return _logLevel;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_logLevel = value;
		}
	}

	public float Opacity
	{
		get
		{
			GuardInitialized();
			return _opacity;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_opacity = Math.Clamp(value, OpacityMin, OpacityMax);
		}
	}

	public BackgroundMode BackgroundMode
	{
		get
		{
			GuardInitialized();
			return _backgroundMode;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_backgroundMode = value;
		}
	}

	public string BackgroundImagePath
	{
		get
		{
			GuardInitialized();
			return _backgroundImagePath;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_backgroundImagePath = value ?? string.Empty;
		}
	}

	public float BackgroundOpacity
	{
		get
		{
			GuardInitialized();
			return _backgroundOpacity;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_backgroundOpacity = Math.Clamp(value, 0f, 1f);
		}
	}

	/// <summary>
	/// 卡片不透明度（0.3..1.0），控制主页等页面卡片的半透明程度。
	/// </summary>
	public float CardOpacity
	{
		get
		{
			GuardInitialized();
			return _cardOpacity;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_cardOpacity = Math.Clamp(value, 0.3f, 1f);
		}
	}

	public ObservableCollection<string> SkipList
	{
		get
		{
			GuardInitialized();
			return _skipList;
		}
	}

	public ObservableCollection<string> OrganizationalFolderNames
	{
		get
		{
			GuardInitialized();
			return _organizationalFolderNames;
		}
	}

	public bool CaseSensitiveSearch
	{
		get
		{
			GuardInitialized();
			return _caseSensitiveSearch;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_caseSensitiveSearch = value;
		}
	}

	/// <summary>
	/// 是否启用搜索框模糊搜索（支持拼音全拼/首字母与字符子序列匹配）。
	/// </summary>
	public bool EnableFuzzySearch
	{
		get
		{
			GuardInitialized();
			return _enableFuzzySearch;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_enableFuzzySearch = value;
		}
	}

	public bool UseSymbolicLinks
	{
		get
		{
			GuardInitialized();
			return _useSymbolicLinks;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_useSymbolicLinks = value;
		}
	}

	public bool DeleteToRecycleBin
	{
		get
		{
			GuardInitialized();
			return _deleteToRecycleBin;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_deleteToRecycleBin = value;
		}
	}

	public bool AutoRemoveMissingMods
	{
		get
		{
			GuardInitialized();
			return _autoRemoveMissingMods;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_autoRemoveMissingMods = value;
		}
	}

	/// <summary>
	/// 启动时自动检查模组版本兼容性
	/// </summary>
	public bool AutoCheckVersionOnStartup
	{
		get
		{
			GuardInitialized();
			return _autoCheckVersionOnStartup;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_autoCheckVersionOnStartup = value;
		}
	}

	/// <summary>
	/// 是否在主页启用批量模组修复入口
	/// </summary>
	public bool EnableBatchRepair
	{
		get
		{
			GuardInitialized();
			return _enableBatchRepair;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_enableBatchRepair = value;
		}
	}

	/// <summary>
	/// 是否已接受模组修复风险及禁止二次分发声明
	/// </summary>
	public bool RepairDisclaimerAccepted
	{
		get
		{
			GuardInitialized();
			return _repairDisclaimerAccepted;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_repairDisclaimerAccepted = value;
		}
	}

	/// <summary>
	/// 是否已完成首次使用引导。
	/// </summary>
	public bool FirstRunTutorialCompleted
	{
		get
		{
			GuardInitialized();
			return _firstRunTutorialCompleted;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_firstRunTutorialCompleted = value;
		}
	}

	/// <summary>
	/// 是否启用日志数量自动清理
	/// </summary>
	public bool AutoCleanLogs
	{
		get
		{
			GuardInitialized();
			return _autoCleanLogs;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_autoCleanLogs = value;
		}
	}

	/// <summary>
	/// 在主页导航面板中显示分隔线
	/// </summary>
	public bool ShowSeparator
	{
		get
		{
			GuardInitialized();
			return _showSeparator;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_showSeparator = value;
		}
	}

	/// <summary>
	/// 是否启用启动时自动识别模组类型并打标签（默认关闭）。
	/// 开启后只会给模组打上已存在的同名标签；是否自动创建缺失标签由 AutoTagCreateMissingTags 控制。
	/// </summary>
	public bool EnableAutoTagging
	{
		get
		{
			GuardInitialized();
			return _enableAutoTagging;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_enableAutoTagging = value;
		}
	}

	/// <summary>
	/// 是否在自动打标签时创建缺失的类型标签（默认关闭）。
	/// 关闭时仅复用用户已有的同名标签（兼容老版本手动创建的标签）。
	/// </summary>
	public bool AutoTagCreateMissingTags
	{
		get
		{
			GuardInitialized();
			return _autoTagCreateMissingTags;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_autoTagCreateMissingTags = value;
		}
	}

	/// <summary>
	/// 手动指定的「自动识别类型 → 标签」配对（默认空）。
	/// 自动打标签时优先使用该配对。
	/// </summary>
	public List<AutoTagMapping> AutoTagMappings
	{
		get
		{
			GuardInitialized();
			return _autoTagMappings;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_autoTagMappings = value;
		}
	}

	/// <summary>
	/// 模组列表分隔符集合
	/// </summary>
	public ObservableCollection<ModSeparator> Separators
	{
		get
		{
			GuardInitialized();
			return _separators;
		}
	}

	/// <summary>
	/// logs 目录中保留的最大日志文件数量
	/// </summary>
	public int MaxLogFiles
	{
		get
		{
			GuardInitialized();
			return _maxLogFiles;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_maxLogFiles = Math.Max(1, value);
		}
	}

	public ObservableCollection<ModTag> Tags
	{
		get
		{
			GuardInitialized();
			return _tags;
		}
	}

	/// <summary>
	/// 部署顺序: false = 从上到下（默认），true = 从下到上
	/// </summary>
	public bool DeployBottomToTop
	{
		get
		{
			GuardInitialized();
			return _deployBottomToTop;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_deployBottomToTop = value;
		}
	}

	/// <summary>
	/// 是否启用自定义部署顺序
	/// true = 部署时使用 DeploymentOrderGuids 的顺序
	/// false = 默认，部署时使用 Dashboard 顺序
	/// </summary>
	public bool UseDeploymentOrder
	{
		get
		{
			GuardInitialized();
			return _useDeploymentOrder;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_useDeploymentOrder = value;
		}
	}

	/// <summary>
	/// 自定义部署顺序的 GUID 列表
	/// 仅当 UseDeploymentOrder = true 时生效
	/// </summary>
	public List<Guid> DeploymentOrderGuids
	{
		get
		{
			GuardInitialized();
			return _deploymentOrderGuids;
		}
	}

	/// <summary>
	/// 界面语言设置。
	/// 空字符串表示自动检测系统语言；
	/// 非空值表示用户手动指定的语言代码（如 "zh-CN", "en-US"）。
	/// </summary>
	public string Language
	{
		get
		{
			GuardInitialized();
			return _language;
		}
		set
		{
			GuardInitialized();
			GuardReadonly();
			_language = value;
		}
	}

	/// <summary>
	/// 模组选项的部署顺序（按 Mod GUID 索引）
	/// value 为选项索引的自定义顺序数组
	/// </summary>
	public Dictionary<Guid, int[]> OptionOrders
	{
		get
		{
			GuardInitialized();
			return _optionOrders;
		}
	}

	/// <summary>
	/// 模组子选项的部署顺序
	/// key: Mod GUID, value: 选项索引 -> 子选项索引的自定义顺序
	/// </summary>
	public Dictionary<Guid, Dictionary<int, int[]>> SubOptionOrders
	{
		get
		{
			GuardInitialized();
			return _subOptionOrders;
		}
	}

	public string? NexusApiKey
	{
		get
		{
			GuardInitialized();
			// 只在需要时解密
			return DecryptString(_encryptedNexusApiKey);
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			// 立即加密存储
			_encryptedNexusApiKey = EncryptString(value);
		}
	}
	
	private static readonly FileInfo s_file = new("settings.json");
	private static readonly byte[] s_optionalEntropy = Encoding.UTF8.GetBytes("Helldivers2ModManager_Entropy_2024");
	private static readonly JsonSerializerOptions s_serializerOptions = new()
	{
		WriteIndented = true,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly ILogger<SettingsService> _logger;
	private readonly Database? _database;
	private readonly PreferenceRepository? _preferences;
	
	[JsonInclude]
	private string _gameDirectory = null!;
	[JsonInclude]
	private string _storageDirectory = null!;
	[JsonInclude]
	private string _tempDirectory = null!;
	[JsonInclude]
	private LogLevel _logLevel;
	[JsonInclude]
	private float _opacity;
	[JsonInclude]
	private ObservableCollection<string> _skipList = null!;
	[JsonInclude]
	private ObservableCollection<string> _organizationalFolderNames = null!;
	[JsonInclude]
	private bool _caseSensitiveSearch;
	[JsonInclude]
	private bool _enableFuzzySearch = true;
	[JsonInclude]
	private bool _useSymbolicLinks;
	[JsonInclude]
	private bool _deleteToRecycleBin = true;
	[JsonInclude]
	private bool _autoRemoveMissingMods;
	[JsonInclude]
	private bool _deployBottomToTop;
	[JsonInclude]
	private bool _autoCheckVersionOnStartup;
	[JsonInclude]
	private bool _enableBatchRepair;
	[JsonInclude]
	private bool _repairDisclaimerAccepted;
	[JsonInclude]
	private bool _firstRunTutorialCompleted;
	[JsonInclude]
	private bool _autoCleanLogs = true;
	[JsonInclude]
	private bool _showSeparator = false;
	[JsonInclude]
	private bool _enableAutoTagging = false;
	[JsonInclude]
	private bool _autoTagCreateMissingTags = false;
	[JsonInclude]
	private List<AutoTagMapping> _autoTagMappings = [];
	[JsonInclude]
	private ObservableCollection<ModSeparator> _separators = null!;
	[JsonInclude]
	private int _maxLogFiles = 20;
	[JsonInclude]
	private ObservableCollection<ModTag> _tags = null!;
	[JsonInclude]
	private string? _encryptedNexusApiKey;
	[JsonInclude]
	private string _language = string.Empty;
	[JsonInclude]
	private BackgroundMode _backgroundMode;
	[JsonInclude]
	private string _backgroundImagePath = string.Empty;
	[JsonInclude]
	private float _backgroundOpacity = 0.6f;
	[JsonInclude]
	private float _cardOpacity = 0.7f;
	[JsonInclude]
	private bool _useDeploymentOrder;
	[JsonInclude]
	private List<Guid> _deploymentOrderGuids = [];
	[JsonInclude]
	private Dictionary<Guid, int[]> _optionOrders = [];
	[JsonInclude]
	private Dictionary<Guid, Dictionary<int, int[]>> _subOptionOrders = [];

	public SettingsService(ILogger<SettingsService> logger, Database database, PreferenceRepository preferences)
	{
		_logger = logger;
		_database = database;
		_preferences = preferences;
	}

	public SettingsService(ILogger<SettingsService> logger)
		: this(logger, null!, null!)
	{
	}

	private string? EncryptString(string? plainText)
	{
		if (string.IsNullOrEmpty(plainText))
			return null;

		try
		{
			byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
			byte[] encryptedBytes = ProtectedData.Protect(
				plainBytes,
				s_optionalEntropy,
				DataProtectionScope.CurrentUser);
			return Convert.ToBase64String(encryptedBytes);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to encrypt sensitive data");
			return null;
		}
	}

	private string? DecryptString(string? encryptedText)
	{
		if (string.IsNullOrEmpty(encryptedText))
			return null;

		try
		{
			byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
			byte[] decryptedBytes = ProtectedData.Unprotect(
				encryptedBytes,
				s_optionalEntropy,
				DataProtectionScope.CurrentUser);
			return Encoding.UTF8.GetString(decryptedBytes);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to decrypt sensitive data");
			return null;
		}
	}

	public async Task<bool> InitAsync(bool @readonly = false)
	{
		if (Initialized)
			return true;

		_logger.LogInformation("Initializing settings service (readonly = {})", @readonly);

		if (_database is null || _preferences is null)
		{
			s_file.Refresh();
			if (!s_file.Exists)
				return false;

			ResetInternal();
			await ReadAsync().ConfigureAwait(false);
			IsReadonly = @readonly;
			Initialized = true;
			CleanExcessLogs();
			return true;
		}

		await _database!.InitializeAsync().ConfigureAwait(false);
		var boot = await BootConfigurationStore.LoadAsync().ConfigureAwait(false);
		var persisted = await _preferences!.GetAppSettingsAsync("settings").ConfigureAwait(false);

		ResetInternal();
		ApplyCoreModel(persisted);
		if (!string.IsNullOrWhiteSpace(boot?.StorageDirectory))
			_storageDirectory = boot.StorageDirectory;
		if (!string.IsNullOrWhiteSpace(boot?.TempDirectory))
			_tempDirectory = boot.TempDirectory;

		IsReadonly = @readonly;
		Initialized = true;
		_logger.LogInformation("Settings service initialization complete");

		// 启动时自动清理超出数量上限的日志
		CleanExcessLogs();

		return true;
	}

	public async Task ReloadAsync()
	{
		_logger.LogInformation("Reloading settings from disk");

		if (_database is null || _preferences is null)
		{
			s_file.Refresh();
			if (!s_file.Exists)
				return;

			ResetInternal();
			await ReadAsync().ConfigureAwait(false);
			Initialized = true;
			return;
		}

		await _database!.InitializeAsync().ConfigureAwait(false);
		var boot = await BootConfigurationStore.LoadAsync().ConfigureAwait(false);
		var persisted = await _preferences!.GetAppSettingsAsync("settings").ConfigureAwait(false);

		ResetInternal();
		ApplyCoreModel(persisted);
		if (!string.IsNullOrWhiteSpace(boot?.StorageDirectory))
			_storageDirectory = boot.StorageDirectory;
		if (!string.IsNullOrWhiteSpace(boot?.TempDirectory))
			_tempDirectory = boot.TempDirectory;

		Initialized = true;
		_logger.LogInformation("Settings reloaded successfully");
	}

	public void InitDefault(bool @readonly = false)
	{
		if (Initialized)
			return;

		_logger.LogInformation("Initializing settings service as default (readonly = {})", @readonly);

		ResetInternal();
		
		IsReadonly = @readonly;
		Initialized = true;
		_logger.LogInformation("Settings service initialization complete");
	}

	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_tags), nameof(_separators))]
	public void Reset()
	{
		GuardInitialized();
		GuardReadonly();
		ResetInternal();
	}

	public async Task SaveAsync()
	{
		GuardInitialized();
		GuardReadonly();

		if (_database is null || _preferences is null)
		{
			var json = JsonSerializer.Serialize(CreateJsonModel(), s_serializerOptions);
			await File.WriteAllTextAsync(s_file.FullName, json);
			SettingsChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		await _database!.InitializeAsync().ConfigureAwait(false);
		await _preferences!.SetAppSettingsAsync("settings", CreateCoreModel()).ConfigureAwait(false);
		await BootConfigurationStore.SaveAsync(new BootConfiguration
		{
			StorageDirectory = _storageDirectory,
			TempDirectory = _tempDirectory,
		}).ConfigureAwait(false);
		SettingsChanged?.Invoke(this, EventArgs.Empty);
	}

	public bool Validate()
	{
		GuardInitialized();

		if (!Directory.Exists(_gameDirectory))
			try
			{
				Directory.CreateDirectory(_gameDirectory);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to create game directory: {Path}", _gameDirectory);
				return false;
			}
		
		if (!Directory.Exists(_storageDirectory))
			try
			{
				Directory.CreateDirectory(_storageDirectory);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to create storage directory: {Path}", _storageDirectory);
				return false;
			}

		if (!Directory.Exists(_tempDirectory))
			try
			{
				Directory.CreateDirectory(_tempDirectory);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Failed to create temp directory: {Path}", _tempDirectory);
				return false;
			}

		if (!Enum.GetValues<LogLevel>().Contains(_logLevel))
		{
			if (IsReadonly)
				return false;
			_logLevel = LogLevel.Warning;
		}

		if (_opacity is > OpacityMax or < OpacityMin)
		{
			if (IsReadonly)
				return false;
			_opacity = Math.Clamp(_opacity, OpacityMin, OpacityMax);
		}

		if (_backgroundOpacity is < 0f or > 1f)
		{
			if (IsReadonly)
				return false;
			_backgroundOpacity = Math.Clamp(_backgroundOpacity, 0f, 1f);
		}

		if (_cardOpacity is < 0.3f or > 1f)
		{
			if (IsReadonly)
				return false;
			_cardOpacity = Math.Clamp(_cardOpacity, 0.3f, 1f);
		}

		var elms = _skipList.Where(static elm => elm.Length != 16).ToArray();
		if (elms.Length != 0)
		{
			if (IsReadonly)
				return false;
			foreach (var elm in elms)
				_skipList.Remove(elm);
		}

		return true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void GuardReadonly()
	{
		if (IsReadonly)
			throw new InvalidOperationException("Object is readonly!");
	}
	
	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_separators))]
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void GuardInitialized()
	{
		if (!Initialized)
			throw new InvalidOperationException("Object not initialized!");
	}

	private async Task ReadAsync()
	{
		await ReadAsyncFallback();
	}

	private AppSettings CreateCoreModel()
	{
		return new AppSettings
		{
			GameDirectory = _gameDirectory,
			StorageDirectory = _storageDirectory,
			TempDirectory = _tempDirectory,
			LogLevel = (int)_logLevel,
			Opacity = _opacity,
			SkipList = [.. _skipList],
			OrganizationalFolderNames = [.. _organizationalFolderNames],
			CaseSensitiveSearch = _caseSensitiveSearch,
			EnableFuzzySearch = _enableFuzzySearch,
			UseSymbolicLinks = _useSymbolicLinks,
			DeleteToRecycleBin = _deleteToRecycleBin,
			AutoRemoveMissingMods = _autoRemoveMissingMods,
			DeployBottomToTop = _deployBottomToTop,
			AutoCheckVersionOnStartup = _autoCheckVersionOnStartup,
			EnableBatchRepair = _enableBatchRepair,
			RepairDisclaimerAccepted = _repairDisclaimerAccepted,
			FirstRunTutorialCompleted = _firstRunTutorialCompleted,
			AutoCleanLogs = _autoCleanLogs,
			ShowSeparator = _showSeparator,
			EnableAutoTagging = _enableAutoTagging,
			AutoTagCreateMissingTags = _autoTagCreateMissingTags,
			AutoTagMappings = [.. _autoTagMappings.Select(static mapping =>
				new AutoTagMappingSetting((int)mapping.Type, mapping.TagId))],
			Separators = [.. _separators.Select(static separator => new SeparatorSetting(
				separator.Id,
				separator.Name,
				separator.Color,
				separator.IsExpanded,
				separator.ModGuids,
				separator.DisplayIndex))],
			MaxLogFiles = _maxLogFiles,
			Tags = [.. _tags.Select(static tag => new TagSetting(tag.Id, tag.Name, tag.Color))],
			NexusApiKey = _encryptedNexusApiKey,
			Language = _language,
			BackgroundMode = (int)_backgroundMode,
			BackgroundImagePath = _backgroundImagePath,
			BackgroundOpacity = _backgroundOpacity,
			CardOpacity = _cardOpacity,
			UseDeploymentOrder = _useDeploymentOrder,
			DeploymentOrderGuids = [.. _deploymentOrderGuids],
			OptionOrders = new Dictionary<Guid, int[]>(_optionOrders),
			SubOptionOrders = _subOptionOrders.ToDictionary(
				static pair => pair.Key,
				static pair => new Dictionary<int, int[]>(pair.Value)),
		};
	}

	private void ApplyCoreModel(AppSettings? settings)
	{
		if (settings is null)
			return;

		_gameDirectory = settings.GameDirectory;
		_storageDirectory = settings.StorageDirectory;
		_tempDirectory = settings.TempDirectory;
		_logLevel = (LogLevel)settings.LogLevel;
		_opacity = Math.Clamp(settings.Opacity, OpacityMin, OpacityMax);
		_skipList = new ObservableCollection<string>(settings.SkipList);
		_organizationalFolderNames = new ObservableCollection<string>(settings.OrganizationalFolderNames);
		_caseSensitiveSearch = settings.CaseSensitiveSearch;
		_enableFuzzySearch = settings.EnableFuzzySearch;
		_useSymbolicLinks = settings.UseSymbolicLinks;
		_deleteToRecycleBin = settings.DeleteToRecycleBin;
		_autoRemoveMissingMods = settings.AutoRemoveMissingMods;
		_deployBottomToTop = settings.DeployBottomToTop;
		_autoCheckVersionOnStartup = settings.AutoCheckVersionOnStartup;
		_enableBatchRepair = settings.EnableBatchRepair;
		_repairDisclaimerAccepted = settings.RepairDisclaimerAccepted;
		_firstRunTutorialCompleted = settings.FirstRunTutorialCompleted;
		_autoCleanLogs = settings.AutoCleanLogs;
		_showSeparator = settings.ShowSeparator;
		_enableAutoTagging = settings.EnableAutoTagging;
		_autoTagCreateMissingTags = settings.AutoTagCreateMissingTags;
		_autoTagMappings = settings.AutoTagMappings.Select(static mapping => new AutoTagMapping
		{
			Type = (ModType)mapping.Type,
			TagId = mapping.TagId,
		}).ToList();
		_separators = new ObservableCollection<ModSeparator>(settings.Separators.Select(static separator => new ModSeparator
		{
			Id = separator.Id,
			Name = separator.Name,
			Color = separator.Color,
			IsExpanded = separator.IsExpanded,
			ModGuids = [.. separator.ModGuids],
			DisplayIndex = separator.DisplayIndex,
		}));
		_maxLogFiles = Math.Max(1, settings.MaxLogFiles);
		_tags = new ObservableCollection<ModTag>(settings.Tags.Select(static tag => new ModTag(tag.Id, tag.Name, tag.Color)));
		_encryptedNexusApiKey = settings.NexusApiKey;
		_language = settings.Language;
		_backgroundMode = (BackgroundMode)settings.BackgroundMode;
		_backgroundImagePath = settings.BackgroundImagePath;
		_backgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0f, 1f);
		_cardOpacity = Math.Clamp(settings.CardOpacity, 0.3f, 1f);
		_useDeploymentOrder = settings.UseDeploymentOrder;
		_deploymentOrderGuids = [.. settings.DeploymentOrderGuids];
		_optionOrders = settings.OptionOrders.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.ToArray());
		_subOptionOrders = settings.SubOptionOrders.ToDictionary(
			static pair => pair.Key,
			static pair => pair.Value.ToDictionary(
				static nestedPair => nestedPair.Key,
				static nestedPair => nestedPair.Value.ToArray()));
	}

	private object CreateJsonModel()
	{
		return new
		{
			GameDirectory = _gameDirectory,
			StorageDirectory = _storageDirectory,
			TempDirectory = _tempDirectory,
			LogLevel = _logLevel,
			Opacity = _opacity,
			SkipList = _skipList,
			OrganizationalFolderNames = _organizationalFolderNames,
			CaseSensitiveSearch = _caseSensitiveSearch,
			EnableFuzzySearch = _enableFuzzySearch,
			UseSymbolicLinks = _useSymbolicLinks,
			DeleteToRecycleBin = _deleteToRecycleBin,
			AutoRemoveMissingMods = _autoRemoveMissingMods,
			DeployBottomToTop = _deployBottomToTop,
			AutoCheckVersionOnStartup = _autoCheckVersionOnStartup,
			EnableBatchRepair = _enableBatchRepair,
			RepairDisclaimerAccepted = _repairDisclaimerAccepted,
			FirstRunTutorialCompleted = _firstRunTutorialCompleted,
			AutoCleanLogs = _autoCleanLogs,
			ShowSeparator = _showSeparator,
			EnableAutoTagging = _enableAutoTagging,
			AutoTagCreateMissingTags = _autoTagCreateMissingTags,
			AutoTagMappings = _autoTagMappings.Select(static m => new
			{
				type = m.Type,
				tagId = m.TagId,
			}).ToList(),
			Separators = _separators.Select(static separator => new
			{
				id = separator.Id,
				name = separator.Name,
				color = separator.Color,
				isExpanded = separator.IsExpanded,
				displayIndex = separator.DisplayIndex,
				modGuids = separator.ModGuids
			}),
			MaxLogFiles = _maxLogFiles,
			Tags = _tags.Select(static tag => new
			{
				id = tag.Id,
				name = tag.Name,
				color = tag.Color
			}),
			NexusApiKey = _encryptedNexusApiKey,
			Language = _language,
			BackgroundMode = _backgroundMode,
			BackgroundImagePath = _backgroundImagePath,
			BackgroundOpacity = _backgroundOpacity,
			CardOpacity = _cardOpacity,
			UseDeploymentOrder = _useDeploymentOrder,
			DeploymentOrderGuids = _deploymentOrderGuids,
			OptionOrders = _optionOrders.Select(static item => new
			{
				key = item.Key,
				value = item.Value
			}),
			SubOptionOrders = _subOptionOrders.Select(static item => new
			{
				key = item.Key,
				value = item.Value
			})
		};
	}

	private async Task ReadAsyncFallback()
	{
		var stream = s_file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
		var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
		{
			AllowTrailingCommas = true,
			CommentHandling = JsonCommentHandling.Skip
		});
		var root = document.RootElement;
		var firstRunTutorialCompletedFound = false;
		if (root.TryGetProperty(nameof(GameDirectory), JsonValueKind.String, out var prop))
			_gameDirectory = prop.GetString()!;
		if (root.TryGetProperty(nameof(StorageDirectory), JsonValueKind.String, out prop))
			_storageDirectory = prop.GetString()!;
		if (root.TryGetProperty(nameof(TempDirectory), JsonValueKind.String, out prop))
			_tempDirectory = prop.GetString()!;
		if (root.TryGetProperty(nameof(LogLevel), JsonValueKind.String, out prop))
			if (Enum.TryParse<LogLevel>(prop.GetString()!, out var value))
				_logLevel = value;
		if (root.TryGetProperty(nameof(Opacity), JsonValueKind.Number, out prop))
			if (prop.TryGetSingle(out var value))
				_opacity = value;
		if (root.TryGetProperty(nameof(SkipList), JsonValueKind.Array, out var arr))
		{
			var list = new List<string>(arr.GetArrayLength());
			
			foreach (var elm in arr.EnumerateArray())
				if (elm.ValueKind == JsonValueKind.String)
				{
					var value = elm.GetString();
					if (value is not null)
						list.Add(value);
				}

			_skipList = new ObservableCollection<string>(list);
		}
		if (root.TryGetProperty(nameof(OrganizationalFolderNames), JsonValueKind.Array, out arr))
		{
			var orgList = new List<string>(arr.GetArrayLength());
			
			foreach (var elm in arr.EnumerateArray())
				if (elm.ValueKind == JsonValueKind.String)
				{
					var value = elm.GetString();
					if (value is not null)
						orgList.Add(value);
				}

			_organizationalFolderNames = new ObservableCollection<string>(orgList);
		}
		if (root.TryGetProperty(nameof(CaseSensitiveSearch), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_caseSensitiveSearch = prop.GetBoolean();
		if (root.TryGetProperty(nameof(EnableFuzzySearch), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_enableFuzzySearch = prop.GetBoolean();
		if (root.TryGetProperty(nameof(UseSymbolicLinks), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_useSymbolicLinks = prop.GetBoolean();
		if (root.TryGetProperty(nameof(DeleteToRecycleBin), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_deleteToRecycleBin = prop.GetBoolean();
		if (root.TryGetProperty(nameof(AutoRemoveMissingMods), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_autoRemoveMissingMods = prop.GetBoolean();
		if (root.TryGetProperty(nameof(DeployBottomToTop), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_deployBottomToTop = prop.GetBoolean();
		if (root.TryGetProperty(nameof(UseDeploymentOrder), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_useDeploymentOrder = prop.GetBoolean();
		if (root.TryGetProperty(nameof(DeploymentOrderGuids), JsonValueKind.Array, out var orderArr))
		{
			var orderList = new List<Guid>(orderArr.GetArrayLength());
			foreach (var elm in orderArr.EnumerateArray())
			{
				if (elm.ValueKind == JsonValueKind.String && Guid.TryParse(elm.GetString(), out var guid))
					orderList.Add(guid);
			}
			_deploymentOrderGuids = orderList;
		}
		_optionOrders = [];
		if (root.TryGetProperty(nameof(OptionOrders), JsonValueKind.Array, out var optArr))
		{
			foreach (var elm in optArr.EnumerateArray())
			{
				if (elm.ValueKind != JsonValueKind.Object) continue;
				if (!elm.TryGetProperty("key", out var keyProp) || keyProp.ValueKind != JsonValueKind.String) continue;
				if (!Guid.TryParse(keyProp.GetString(), out var guid)) continue;
				if (!elm.TryGetProperty("value", out var valProp) || valProp.ValueKind != JsonValueKind.Array) continue;
				var list = new List<int>();
				foreach (var v in valProp.EnumerateArray())
					if (v.TryGetInt32(out var vi)) list.Add(vi);
				_optionOrders[guid] = [.. list];
			}
		}
		_subOptionOrders = [];
		if (root.TryGetProperty(nameof(SubOptionOrders), JsonValueKind.Array, out var subArr))
		{
			foreach (var elm in subArr.EnumerateArray())
			{
				if (elm.ValueKind != JsonValueKind.Object) continue;
				if (!elm.TryGetProperty("key", out var keyProp) || keyProp.ValueKind != JsonValueKind.String) continue;
				if (!Guid.TryParse(keyProp.GetString(), out var guid)) continue;
				if (!elm.TryGetProperty("value", out var valProp) || valProp.ValueKind != JsonValueKind.Object) continue;
				var innerDict = new Dictionary<int, int[]>();
				foreach (var inner in valProp.EnumerateObject())
				{
					if (int.TryParse(inner.Name, out var optIdx) && inner.Value.ValueKind == JsonValueKind.Array)
					{
						var subList = new List<int>();
						foreach (var v in inner.Value.EnumerateArray())
							if (v.TryGetInt32(out var vi)) subList.Add(vi);
						innerDict[optIdx] = [.. subList];
					}
				}
				_subOptionOrders[guid] = innerDict;
			}
		}
		if (root.TryGetProperty(nameof(AutoCheckVersionOnStartup), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_autoCheckVersionOnStartup = prop.GetBoolean();
		if (root.TryGetProperty(nameof(EnableBatchRepair), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_enableBatchRepair = prop.GetBoolean();
		if (root.TryGetProperty(nameof(RepairDisclaimerAccepted), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_repairDisclaimerAccepted = prop.GetBoolean();
		if (root.TryGetProperty(nameof(FirstRunTutorialCompleted), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
		{
			_firstRunTutorialCompleted = prop.GetBoolean();
			firstRunTutorialCompletedFound = true;
		}
		if (root.TryGetProperty(nameof(AutoCleanLogs), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_autoCleanLogs = prop.GetBoolean();
		if (root.TryGetProperty(nameof(ShowSeparator), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_showSeparator = prop.GetBoolean();
	if (root.TryGetProperty(nameof(EnableAutoTagging), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
		_enableAutoTagging = prop.GetBoolean();
	if (root.TryGetProperty(nameof(AutoTagCreateMissingTags), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
		_autoTagCreateMissingTags = prop.GetBoolean();
	if (root.TryGetProperty(nameof(AutoTagMappings), JsonValueKind.Array, out var mappingArr))
	{
		var mappingList = new List<AutoTagMapping>();
		foreach (var mappingElm in mappingArr.EnumerateArray())
		{
			if (!mappingElm.TryGetProperty(nameof(AutoTagMapping.Type), out var typeProp) || typeProp.ValueKind != JsonValueKind.Number)
				continue;
			if (!mappingElm.TryGetProperty(nameof(AutoTagMapping.TagId), out var tagIdProp) || tagIdProp.ValueKind != JsonValueKind.String)
				continue;
			if (!Guid.TryParse(tagIdProp.GetString(), out var mappingTagId))
				continue;
			mappingList.Add(new AutoTagMapping { Type = (ModType)typeProp.GetInt32(), TagId = mappingTagId });
		}
		_autoTagMappings = mappingList;
	}
		if (root.TryGetProperty(nameof(Separators), JsonValueKind.Array, out var sepArr))
		{
			var sepList = new List<ModSeparator>();
			foreach (var sepElm in sepArr.EnumerateArray())
			{
				if (sepElm.ValueKind != JsonValueKind.Object) continue;
				try
				{
					var id = Guid.Parse(sepElm.GetProperty("id").GetString()!);
					var name = sepElm.GetProperty("name").GetString() ?? "分隔符";
					var color = sepElm.TryGetProperty("color", out var colorProp) ? colorProp.GetString() : "#FF6200EE";
					var isExpanded = sepElm.TryGetProperty("isExpanded", out var expandedProp) && expandedProp.GetBoolean();
					var displayIndex = sepElm.TryGetProperty("displayIndex", out var diProp) ? diProp.GetInt32() : -1;

					var modGuids = new List<Guid>();
					if (sepElm.TryGetProperty("modGuids", out var guidsArr) && guidsArr.ValueKind == JsonValueKind.Array)
					{
						foreach (var g in guidsArr.EnumerateArray())
						{
							if (g.ValueKind == JsonValueKind.String && Guid.TryParse(g.GetString(), out var mg))
								modGuids.Add(mg);
						}
					}

					sepList.Add(new ModSeparator
					{
						Id = id,
						Name = name,
						Color = color ?? "#FF6200EE",
						IsExpanded = isExpanded,
						DisplayIndex = displayIndex,
						ModGuids = modGuids
					});
				}
				catch { continue; }
			}
			_separators = new ObservableCollection<ModSeparator>(sepList);
		}
		if (root.TryGetProperty(nameof(MaxLogFiles), JsonValueKind.Number, out prop))
			_maxLogFiles = Math.Max(1, prop.GetInt32());
		if (root.TryGetProperty(nameof(Language), JsonValueKind.String, out prop))
			_language = prop.GetString() ?? string.Empty;
		if (root.TryGetProperty(nameof(BackgroundMode), JsonValueKind.Number, out prop))
			_backgroundMode = (BackgroundMode)prop.GetInt32();
		if (root.TryGetProperty(nameof(BackgroundImagePath), JsonValueKind.String, out prop))
			_backgroundImagePath = prop.GetString() ?? string.Empty;
		if (root.TryGetProperty(nameof(BackgroundOpacity), JsonValueKind.Number, out prop))
			if (prop.TryGetSingle(out var backgroundOpacity))
				_backgroundOpacity = Math.Clamp(backgroundOpacity, 0f, 1f);
		if (root.TryGetProperty(nameof(CardOpacity), JsonValueKind.Number, out prop))
			if (prop.TryGetSingle(out var cardOpacity))
				_cardOpacity = Math.Clamp(cardOpacity, 0.3f, 1f);
		if (root.TryGetProperty(nameof(Tags), JsonValueKind.Array, out var tagsArr))
		{
			var tagsList = new List<ModTag>();
			foreach (var tagElm in tagsArr.EnumerateArray())
			{
				if (tagElm.ValueKind == JsonValueKind.Object)
				{
					try
					{
						var id = Guid.Parse(tagElm.GetProperty("id").GetString()!);
						var name = tagElm.GetProperty("name").GetString()!;
						var color = tagElm.TryGetProperty("color", out var colorProp) ? colorProp.GetString()! : "#FF6200EE";
						tagsList.Add(new ModTag(id, name, color));
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to parse tag, skipping");
					}
				}
			}
			_tags = new ObservableCollection<ModTag>(tagsList);
		}
		if (root.TryGetProperty(nameof(NexusApiKey), JsonValueKind.String, out prop))
			_encryptedNexusApiKey = prop.GetString();

		if (!firstRunTutorialCompletedFound)
			_firstRunTutorialCompleted = true;

		document.Dispose();
		await stream.DisposeAsync();
	}

	/// <summary>
	/// 清理超出数量上限的日志文件。
	/// logs 目录中只保留最新的 MaxLogFiles 个日志。
	/// </summary>
	public void CleanExcessLogs()
	{
		if (!_autoCleanLogs)
			return;

		try
		{
			var logDir = new DirectoryInfo("logs");
			if (!logDir.Exists)
				return;

			var filesToDelete = logDir
				.EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
				.OrderByDescending(static file => file.LastWriteTimeUtc)
				.ThenByDescending(static file => file.CreationTimeUtc)
				.Skip(_maxLogFiles)
				.ToArray();
			int deleted = 0;

			foreach (var file in filesToDelete)
			{
				try
				{
					file.Delete();
					deleted++;
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Failed to delete excess log file: {File}", file.Name);
				}
			}

			if (deleted > 0)
				_logger.LogInformation("Cleaned {Count} excess log files (maximum retained: {MaxFiles})", deleted, _maxLogFiles);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean excess log files");
		}
	}

	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_tags), nameof(_separators))]
	private void ResetInternal()
	{
		_gameDirectory = string.Empty;
		_storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helldivers2ModManager");
		_tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "Helldivers2ModManager");
		_logLevel = LogLevel.Warning;
		_opacity = 0.8f;
		_backgroundMode = BackgroundMode.Default;
		_backgroundImagePath = string.Empty;
		_backgroundOpacity = 0.6f;
		_cardOpacity = 0.7f;
		_skipList = [];
		_caseSensitiveSearch = false;
		_enableFuzzySearch = true;
		_useSymbolicLinks = false;
		_autoRemoveMissingMods = false;
		_deleteToRecycleBin = true;
		_autoCheckVersionOnStartup = false;
		_enableBatchRepair = false;
		_repairDisclaimerAccepted = false;
		_firstRunTutorialCompleted = false;
		_autoCleanLogs = true;
		_maxLogFiles = 20;
		_showSeparator = false;
		_enableAutoTagging = false;
		_autoTagCreateMissingTags = false;
		_autoTagMappings = [];
		_separators = [];
		_tags = [];
		_organizationalFolderNames = ["Models", "Model"];
		_encryptedNexusApiKey = null;
		_useDeploymentOrder = false;
		_deploymentOrderGuids = [];
		_optionOrders = [];
		_subOptionOrders = [];
	}
}

/// <summary>
/// 窗口背景模式。
/// </summary>
internal enum BackgroundMode
{
	Default = 0,
	Image = 1
}
