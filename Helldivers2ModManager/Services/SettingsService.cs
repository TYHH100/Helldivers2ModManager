using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Helldivers2ModManager.Extensions;
using Helldivers2ModManager.Core.Settings;
using Helldivers2ModManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Services;

[RegisterService(ServiceLifetime.Singleton)]
internal sealed class SettingsService
{
    private const int CurrentSchemaVersion = 2;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly ISettingsStore _settingsStore;
    public const float OpacityMax = 1.0f;

    public const float OpacityMin = 0.4f;

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
    /// Enables the local browser-extension endpoint. Disabled by default until
    /// the user explicitly opts in and pairs an extension.
    /// </summary>
    public bool EnableBrowserIntegration
    {
        get
        {
            GuardInitialized();
            return _enableBrowserIntegration;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _enableBrowserIntegration = value;
        }
    }

    /// <summary>
    /// Enables experimental binary repair operations. This safety gate is
    /// independent from the visibility preference for batch repair.
    /// </summary>
    public bool EnableExperimentalRepair
    {
        get
        {
            GuardInitialized();
            return _enableExperimentalRepair;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _enableExperimentalRepair = value;
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

    public string Theme
    {
        get
        {
            GuardInitialized();
            return _theme;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _theme = value is "Light" or "Dark" ? value : "System";
        }
    }

    public bool EnableAnimations
    {
        get
        {
            GuardInitialized();
            return _enableAnimations;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _enableAnimations = value;
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

    public string BrowserExtensionTokenHash
    {
        get
        {
            GuardInitialized();
            return _browserExtensionTokenHash;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _browserExtensionTokenHash = value;
        }
    }

    public string BrowserExtensionOrigin
    {
        get
        {
            GuardInitialized();
            return _browserExtensionOrigin;
        }
        set
        {
            GuardInitialized();
            GuardReadonly();
            _browserExtensionOrigin = value;
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

    private static readonly FileInfo s_file = new(Path.Combine(AppContext.BaseDirectory, "settings.json"));
    private static readonly byte[] s_optionalEntropy = Encoding.UTF8.GetBytes("Helldivers2ModManager_Entropy_2024");
    private readonly ILogger<SettingsService> _logger;

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
    private bool _useSymbolicLinks;
    [JsonInclude]
    private bool _deleteToRecycleBin = true;
    [JsonInclude]
    private bool _autoRemoveMissingMods;
    [JsonInclude]
    private bool _enableSorting;
    [JsonInclude]
    private bool _deployBottomToTop;
    [JsonInclude]
    private bool _autoCheckVersionOnStartup;
    [JsonInclude]
    private bool _enableBatchRepair;
    [JsonInclude]
    private bool _enableBrowserIntegration;
    [JsonInclude]
    private bool _enableExperimentalRepair;
    [JsonInclude]
    private bool _repairDisclaimerAccepted;
    [JsonInclude]
    private bool _autoCleanLogs = true;
    [JsonInclude]
    private bool _showSeparator = true;
    [JsonInclude]
    private ObservableCollection<ModSeparator> _separators = null!;
    [JsonInclude]
    private int _logRetentionDays = 7;
    [JsonInclude]
    private ObservableCollection<ModTag> _tags = null!;
    [JsonInclude]
    private string? _encryptedNexusApiKey;
    [JsonInclude]
    private string _extensionHost = "localhost";
    [JsonInclude]
    private int _extensionPort = 7456;
    [JsonInclude]
    private string _browserExtensionTokenHash = string.Empty;
    [JsonInclude]
    private string _browserExtensionOrigin = string.Empty;
    [JsonInclude]
    private string _language = string.Empty;
    [JsonInclude]
    private string _theme = "System";
    [JsonInclude]
    private bool _enableAnimations = true;
    [JsonInclude]
    private bool _useDeploymentOrder;
    [JsonInclude]
    private List<Guid> _deploymentOrderGuids = [];
    [JsonInclude]
    private Dictionary<Guid, int[]> _optionOrders = [];
    [JsonInclude]
    private Dictionary<Guid, Dictionary<int, int[]>> _subOptionOrders = [];

    public SettingsService(ILogger<SettingsService> logger, ISettingsStore settingsStore)
    {
        _logger = logger;
        _settingsStore = settingsStore;
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

    public async Task<bool> InitAsync(
        bool @readonly = false,
        CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Initialized)
                return true;

            _logger.LogInformation("Initializing settings service (readonly = {})", @readonly);
            s_file.Refresh();
            var legacyPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "settings.json"));
            if (!s_file.Exists &&
                (string.Equals(legacyPath, s_file.FullName, StringComparison.OrdinalIgnoreCase) || !File.Exists(legacyPath)))
                return false;

            ResetInternal();
            ApplySnapshot(await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false));

            IsReadonly = @readonly;
            Initialized = true;
            _logger.LogInformation("Settings service initialization complete");

            CleanOldLogs();
            return true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task ReloadAsync()
    {
        _logger.LogInformation("Reloading settings from disk");

        ResetInternal();
        ApplySnapshot(await _settingsStore.LoadAsync(CancellationToken.None));

        Initialized = true;
        _logger.LogInformation("Settings reloaded successfully");
    }

    public void InitDefault(bool @readonly = false)
    {
        _initializationLock.Wait();
        try
        {
            if (Initialized)
                return;

            _logger.LogInformation("Initializing settings service as default (readonly = {})", @readonly);
            ResetInternal();
            IsReadonly = @readonly;
            Initialized = true;
            _logger.LogInformation("Settings service initialization complete");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    [MemberNotNull(nameof(_gameDirectory), nameof(_storageDirectory), nameof(_tempDirectory), nameof(_skipList), nameof(_organizationalFolderNames), nameof(_tags), nameof(_separators))]
    public void Reset()
    {
        GuardInitialized();
        GuardReadonly();
        ResetInternal();
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        GuardInitialized();
        GuardReadonly();

        await _settingsStore.SaveAsync(CreateSnapshot(), cancellationToken).ConfigureAwait(false);
        s_file.Refresh();
    }

    public bool Validate()
    {
        GuardInitialized();

        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
            return false;

        var requiredGamePaths = new[]
        {
            Path.Combine(_gameDirectory, "data"),
            Path.Combine(_gameDirectory, "tools"),
            Path.Combine(_gameDirectory, "bin"),
            Path.Combine(_gameDirectory, "bin", "helldivers2.exe")
        };
        if (requiredGamePaths.Take(3).Any(static path => !Directory.Exists(path)) ||
            !File.Exists(requiredGamePaths[3]))
            return false;

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

    private AppSettingsSnapshot CreateSnapshot() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        GameDirectory = _gameDirectory,
        StorageDirectory = _storageDirectory,
        TempDirectory = _tempDirectory,
        LogLevel = _logLevel.ToString(),
        Opacity = _opacity,
        SkipList = _skipList.ToArray(),
        OrganizationalFolderNames = _organizationalFolderNames.ToArray(),
        CaseSensitiveSearch = _caseSensitiveSearch,
        UseSymbolicLinks = _useSymbolicLinks,
        DeleteToRecycleBin = _deleteToRecycleBin,
        AutoRemoveMissingMods = _autoRemoveMissingMods,
        EnableSorting = _enableSorting,
        DeployBottomToTop = _deployBottomToTop,
        AutoCheckVersionOnStartup = _autoCheckVersionOnStartup,
        EnableBatchRepair = _enableBatchRepair,
        EnableBrowserIntegration = _enableBrowserIntegration,
        EnableExperimentalRepair = _enableExperimentalRepair,
        RepairDisclaimerAccepted = _repairDisclaimerAccepted,
        AutoCleanLogs = _autoCleanLogs,
        ShowSeparator = _showSeparator,
        LogRetentionDays = _logRetentionDays,
        Separators = _separators.Select(static separator => new SeparatorSettingsSnapshot(
            separator.Id,
            separator.Name,
            separator.Color,
            separator.IsExpanded,
            separator.DisplayIndex,
            separator.ModGuids.ToArray())).ToArray(),
        Tags = _tags.Select(static tag => new TagSettingsSnapshot(tag.Id, tag.Name, tag.Color)).ToArray(),
        NexusApiKey = _encryptedNexusApiKey,
        ExtensionHost = _extensionHost,
        ExtensionPort = _extensionPort,
        BrowserExtensionTokenHash = _browserExtensionTokenHash,
        BrowserExtensionOrigin = _browserExtensionOrigin,
        Language = _language,
        Theme = _theme,
        EnableAnimations = _enableAnimations,
        UseDeploymentOrder = _useDeploymentOrder,
        DeploymentOrderGuids = _deploymentOrderGuids.ToArray(),
        OptionOrders = _optionOrders.Select(static item =>
            new OptionOrderSettingsSnapshot(item.Key, item.Value)).ToArray(),
        SubOptionOrders = _subOptionOrders.Select(static item =>
            new SubOptionOrderSettingsSnapshot(
                item.Key,
                item.Value.ToDictionary(
                    static pair => pair.Key,
                    static pair => (IReadOnlyList<int>)pair.Value))).ToArray()
    };

    private void ApplySnapshot(AppSettingsSnapshot snapshot)
    {
        _gameDirectory = snapshot.GameDirectory;
        _storageDirectory = snapshot.StorageDirectory;
        _tempDirectory = snapshot.TempDirectory;
        if (Enum.TryParse<LogLevel>(snapshot.LogLevel, ignoreCase: true, out var logLevel))
            _logLevel = logLevel;
        _opacity = snapshot.Opacity;
        _skipList = new ObservableCollection<string>(snapshot.SkipList);
        _organizationalFolderNames = new ObservableCollection<string>(snapshot.OrganizationalFolderNames);
        _caseSensitiveSearch = snapshot.CaseSensitiveSearch;
        _useSymbolicLinks = snapshot.UseSymbolicLinks;
        _deleteToRecycleBin = snapshot.DeleteToRecycleBin;
        _autoRemoveMissingMods = snapshot.AutoRemoveMissingMods;
        _enableSorting = snapshot.EnableSorting;
        _deployBottomToTop = snapshot.DeployBottomToTop;
        _autoCheckVersionOnStartup = snapshot.AutoCheckVersionOnStartup;
        _enableBatchRepair = snapshot.EnableBatchRepair;
        _enableBrowserIntegration = snapshot.EnableBrowserIntegration;
        _enableExperimentalRepair = snapshot.EnableExperimentalRepair;
        _repairDisclaimerAccepted = snapshot.RepairDisclaimerAccepted;
        _autoCleanLogs = snapshot.AutoCleanLogs;
        _showSeparator = snapshot.ShowSeparator;
        _logRetentionDays = snapshot.LogRetentionDays;
        _separators = new ObservableCollection<ModSeparator>(snapshot.Separators.Select(static separator => new ModSeparator
        {
            Id = separator.Id,
            Name = separator.Name,
            Color = separator.Color,
            IsExpanded = separator.IsExpanded,
            DisplayIndex = separator.DisplayIndex,
            ModGuids = separator.ModGuids.ToList()
        }));
        _tags = new ObservableCollection<ModTag>(snapshot.Tags.Select(static tag => new ModTag(tag.Id, tag.Name, tag.Color)));
        _encryptedNexusApiKey = snapshot.NexusApiKey;
        _extensionHost = snapshot.ExtensionHost;
        _extensionPort = snapshot.ExtensionPort;
        _browserExtensionTokenHash = snapshot.BrowserExtensionTokenHash;
        _browserExtensionOrigin = snapshot.BrowserExtensionOrigin;
        _language = snapshot.Language;
        _theme = snapshot.Theme;
        _enableAnimations = snapshot.EnableAnimations;
        _useDeploymentOrder = snapshot.UseDeploymentOrder;
        _deploymentOrderGuids = snapshot.DeploymentOrderGuids.ToList();
        _optionOrders = snapshot.OptionOrders.ToDictionary(
            static item => item.Key,
            static item => item.Value.ToArray());
        _subOptionOrders = snapshot.SubOptionOrders.ToDictionary(
            static item => item.Key,
            static item => item.Value.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToArray()));
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
        _deployBottomToTop = false;
        _autoCheckVersionOnStartup = false;
        _enableBatchRepair = false;
        _enableBrowserIntegration = false;
        _enableExperimentalRepair = false;
        _repairDisclaimerAccepted = false;
        _autoCleanLogs = true;
        _showSeparator = true;
        _logRetentionDays = 7;
        _separators = [];
        _extensionHost = "localhost";
        _extensionPort = 7456;
        _browserExtensionTokenHash = string.Empty;
        _browserExtensionOrigin = string.Empty;
        _language = string.Empty;
        _tags = [];
        _organizationalFolderNames = ["Models", "Model"];
        _encryptedNexusApiKey = null;
        _useDeploymentOrder = false;
        _deploymentOrderGuids = [];
        _optionOrders = [];
        _subOptionOrders = [];
        _theme = "System";
        _enableAnimations = true;
    }
}
