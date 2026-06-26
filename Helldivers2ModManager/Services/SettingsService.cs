using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class SettingsService
{
	public const float OpacityMax = 1.0f;
	
	public const float OpacityMin = 0.4f;
	
	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_separators))]
	public bool Initialized { get; private set; }
	
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

	public bool EnableSorting
	{
		get
		{
			GuardInitialized();
			return _enableSorting;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_enableSorting = value;
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
	/// 是否启用自动清理过期日志
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
	/// 日志保留天数（超过此天数的日志将被清理）
	/// </summary>
	public int LogRetentionDays
	{
		get
		{
			GuardInitialized();
			return _logRetentionDays;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_logRetentionDays = Math.Max(1, value);
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

	public string ExtensionHost
	{
		get
		{
			GuardInitialized();
			return _extensionHost;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_extensionHost = value;
		}
	}

	public int ExtensionPort
	{
		get
		{
			GuardInitialized();
			return _extensionPort;
		}

		set
		{
			GuardInitialized();
			GuardReadonly();
			_extensionPort = value;
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
	private static readonly JsonDocumentOptions s_options = new()
	{
		AllowTrailingCommas = true,
		CommentHandling = JsonCommentHandling.Skip
	};
	private static readonly byte[] s_optionalEntropy = Encoding.UTF8.GetBytes("Helldivers2ModManager_Entropy_2024");

	private readonly ILogger<SettingsService> _logger;
	private string _gameDirectory = null!;
	private string _storageDirectory = null!;
	private string _tempDirectory = null!;
	private LogLevel _logLevel;
	private float _opacity;
	private ObservableCollection<string> _skipList = null!;
	private ObservableCollection<string> _organizationalFolderNames = null!;
	private bool _caseSensitiveSearch;
	private bool _useSymbolicLinks;
	private bool _deleteToRecycleBin = true;
	private bool _autoRemoveMissingMods;
	private bool _enableSorting;
	private bool _deployBottomToTop;
	private bool _autoCheckVersionOnStartup;
	private bool _autoCleanLogs = true;
	private bool _showSeparator = true;
	private ObservableCollection<ModSeparator> _separators = null!;
	private int _logRetentionDays = 7;
	private ObservableCollection<ModTag> _tags = null!;
	private string? _encryptedNexusApiKey;
	private string _extensionHost = "localhost";
	private int _extensionPort = 7456;
	private string _language = string.Empty;
	private bool _useDeploymentOrder;
	private List<Guid> _deploymentOrderGuids = [];
	private Dictionary<Guid, int[]> _optionOrders = [];
	private Dictionary<Guid, Dictionary<int, int[]>> _subOptionOrders = [];

	public SettingsService(ILogger<SettingsService> logger)
	{
		_logger = logger;
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
		
		s_file.Refresh();
		if (!s_file.Exists)
			return false;

		ResetInternal();

		await ReadAsync();
		
		IsReadonly = @readonly;
		Initialized = true;
		_logger.LogInformation("Settings service initialization complete");

		// 启动时自动清理过期日志
		CleanOldLogs();

		return true;
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

		var stream = s_file.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
		var writer = new Utf8JsonWriter(stream);
		
		writer.WriteStartObject();
			writer.WriteString(nameof(GameDirectory), _gameDirectory);
			writer.WriteString(nameof(StorageDirectory), _storageDirectory);
			writer.WriteString(nameof(TempDirectory), _tempDirectory);
			writer.WriteString(nameof(LogLevel), _logLevel.ToString());
			writer.WriteNumber(nameof(Opacity), _opacity);
				writer.WriteStartArray(nameof(SkipList));
			foreach (var elm in _skipList)
				writer.WriteStringValue(elm);
			writer.WriteEndArray();
			writer.WriteStartArray(nameof(OrganizationalFolderNames));
			foreach (var elm in _organizationalFolderNames)
				writer.WriteStringValue(elm);
			writer.WriteEndArray();
			writer.WriteBoolean(nameof(CaseSensitiveSearch), _caseSensitiveSearch);
			writer.WriteBoolean(nameof(UseSymbolicLinks), _useSymbolicLinks);
			writer.WriteBoolean(nameof(DeleteToRecycleBin), _deleteToRecycleBin);
			writer.WriteBoolean(nameof(AutoRemoveMissingMods), _autoRemoveMissingMods);
			writer.WriteBoolean(nameof(EnableSorting), _enableSorting);
			writer.WriteBoolean(nameof(DeployBottomToTop), _deployBottomToTop);
			writer.WriteBoolean(nameof(UseDeploymentOrder), _useDeploymentOrder);
			writer.WriteStartArray(nameof(DeploymentOrderGuids));
			foreach (var guid in _deploymentOrderGuids)
				writer.WriteStringValue(guid.ToString());
			writer.WriteEndArray();
			writer.WriteStartArray(nameof(OptionOrders));
			foreach (var kvp in _optionOrders)
			{
				writer.WriteStartObject();
				writer.WriteString("key", kvp.Key.ToString());
				writer.WriteStartArray("value");
				foreach (var idx in kvp.Value)
					writer.WriteNumberValue(idx);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteStartArray(nameof(SubOptionOrders));
			foreach (var kvp in _subOptionOrders)
			{
				writer.WriteStartObject();
				writer.WriteString("key", kvp.Key.ToString());
				writer.WriteStartObject("value");
				foreach (var innerKvp in kvp.Value)
				{
					writer.WriteStartArray(innerKvp.Key.ToString());
					foreach (var idx in innerKvp.Value)
						writer.WriteNumberValue(idx);
					writer.WriteEndArray();
				}
				writer.WriteEndObject();
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteBoolean(nameof(AutoCheckVersionOnStartup), _autoCheckVersionOnStartup);
			writer.WriteBoolean(nameof(AutoCleanLogs), _autoCleanLogs);
			writer.WriteBoolean(nameof(ShowSeparator), _showSeparator);
			writer.WriteStartArray(nameof(Separators));
			foreach (var sep in _separators)
			{
				writer.WriteStartObject();
				writer.WriteString("id", sep.Id.ToString());
				writer.WriteString("name", sep.Name);
				writer.WriteString("color", sep.Color);
				writer.WriteBoolean("isExpanded", sep.IsExpanded);
				writer.WriteNumber("displayIndex", sep.DisplayIndex);
				writer.WriteStartArray("modGuids");
				foreach (var guid in sep.ModGuids)
					writer.WriteStringValue(guid.ToString());
				writer.WriteEndArray();
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			writer.WriteNumber(nameof(LogRetentionDays), _logRetentionDays);
			writer.WriteString(nameof(ExtensionHost), _extensionHost);
			writer.WriteNumber(nameof(ExtensionPort), _extensionPort);
			writer.WriteString(nameof(Language), _language);
			writer.WriteStartArray(nameof(Tags));
			foreach (var tag in _tags)
			{
				writer.WriteStartObject();
				writer.WriteString("id", tag.Id.ToString());
				writer.WriteString("name", tag.Name);
				writer.WriteString("color", tag.Color);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
			if (_encryptedNexusApiKey is not null)
				writer.WriteString(nameof(NexusApiKey), _encryptedNexusApiKey);
		writer.WriteEndObject();
		
		await writer.DisposeAsync();
		await stream.DisposeAsync();
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
			_logLevel = LogLevel.Trace;
		}

		if (_opacity is > OpacityMax or < OpacityMin)
		{
			if (IsReadonly)
				return false;
			_opacity = Math.Clamp(_opacity, OpacityMin, OpacityMax);
		}

		var elms = _skipList.Where(static elm => elm.Length != 16).ToArray();
		if (elms.Length != 0)
		{
			if (IsReadonly)
				return false;
			foreach (var elm in elms)
				_skipList.Remove(elm);
		}

		if (string.IsNullOrWhiteSpace(_extensionHost))
		{
			if (IsReadonly)
				return false;
			_extensionHost = "localhost";
		}

		if (_extensionPort is < 1 or > 65535)
		{
			if (IsReadonly)
				return false;
			_extensionPort = 7456;
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
		var stream = s_file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
		var document = await JsonDocument.ParseAsync(stream, s_options);
		var root = document.RootElement;
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
		if (root.TryGetProperty(nameof(UseSymbolicLinks), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_useSymbolicLinks = prop.GetBoolean();
		if (root.TryGetProperty(nameof(DeleteToRecycleBin), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_deleteToRecycleBin = prop.GetBoolean();
		if (root.TryGetProperty(nameof(AutoRemoveMissingMods), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_autoRemoveMissingMods = prop.GetBoolean();
		if (root.TryGetProperty(nameof(EnableSorting), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_enableSorting = prop.GetBoolean();
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
		if (root.TryGetProperty(nameof(AutoCleanLogs), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_autoCleanLogs = prop.GetBoolean();
		if (root.TryGetProperty(nameof(ShowSeparator), out prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
			_showSeparator = prop.GetBoolean();
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
		if (root.TryGetProperty(nameof(LogRetentionDays), JsonValueKind.Number, out prop))
			_logRetentionDays = Math.Max(1, prop.GetInt32());
		if (root.TryGetProperty(nameof(ExtensionHost), JsonValueKind.String, out prop))
			_extensionHost = prop.GetString()!;
		if (root.TryGetProperty(nameof(ExtensionPort), JsonValueKind.Number, out prop))
			if (prop.TryGetInt32(out var portValue))
				_extensionPort = portValue;
		if (root.TryGetProperty(nameof(Language), JsonValueKind.String, out prop))
			_language = prop.GetString() ?? string.Empty;
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

		document.Dispose();
		await stream.DisposeAsync();
	}

	/// <summary>
	/// 清理过期日志文件。
	/// 根据 LogRetentionDays 删除 logs 目录中超过指定天数的日志文件。
	/// </summary>
	public void CleanOldLogs()
	{
		if (!_autoCleanLogs)
			return;

		try
		{
			var logDir = new DirectoryInfo("logs");
			if (!logDir.Exists)
				return;

			var cutoff = DateTime.UtcNow.AddDays(-_logRetentionDays);
			int deleted = 0;

			foreach (var file in logDir.EnumerateFiles("*.log"))
			{
				if (file.LastWriteTimeUtc < cutoff)
				{
					try
					{
						file.Delete();
						deleted++;
					}
					catch (Exception ex)
					{
						_logger.LogWarning(ex, "Failed to delete old log file: {File}", file.Name);
					}
				}
			}

			if (deleted > 0)
				_logger.LogInformation("Cleaned {Count} old log files (retention: {Days} days)", deleted, _logRetentionDays);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Failed to clean old log files");
		}
	}

	[MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_tags), nameof(_separators))]
	private void ResetInternal()
	{
		_gameDirectory = string.Empty;
		_storageDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Helldivers2ModManager");
		_tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "Helldivers2ModManager");
		_logLevel = LogLevel.Trace;
		_opacity = 0.8f;
		_skipList = [];
		_caseSensitiveSearch = false;
		_useSymbolicLinks = false;
    _autoRemoveMissingMods = false;
    _deleteToRecycleBin = true;
    _enableSorting = false;
    _autoCheckVersionOnStartup = false;
		_showSeparator = true;
		_separators = [];
		_extensionHost = "localhost";
		_extensionPort = 7456;
		_tags = [];
		_organizationalFolderNames = ["Models", "Model"];
		_encryptedNexusApiKey = null;
		_useDeploymentOrder = false;
		_deploymentOrderGuids = [];
		_optionOrders = [];
		_subOptionOrders = [];
	}
}