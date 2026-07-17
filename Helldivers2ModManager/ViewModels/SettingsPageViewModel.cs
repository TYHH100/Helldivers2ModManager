using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Core.Operations;
using Helldivers2ModManager.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class SettingsPageViewModel : PageViewModelBase
{
    public override string Title => _localizationService["SettingsPage.Title"];

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
            if (value && !IsRunningAsAdministrator())
            {
                value = false;
                IsSymbolicLinkAdminWarningVisible = true;
            }
            else
            {
                IsSymbolicLinkAdminWarningVisible = false;
            }

            OnPropertyChanging();
            _settingsService.UseSymbolicLinks = value;
            OnPropertyChanged();
        }
    }

    public string SymbolicLinkAdminWarning => _localizationService["SettingsPage.SymbolicLinkAdminMsg"];

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

    public bool EnableBrowserIntegration
    {
        get => _settingsService.Initialized && _settingsService.EnableBrowserIntegration;
        set
        {
            OnPropertyChanging();
            _settingsService.EnableBrowserIntegration = value;
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

    private bool _restartRequired;

    public string ExtensionHost
    {
        get => _settingsService.Initialized ? _settingsService.ExtensionHost : "localhost";
        set
        {
            OnPropertyChanging();
            _settingsService.ExtensionHost = value;
            OnPropertyChanged();

            // 标记需要重启才能生效
            _restartRequired = true;
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

            // 标记需要重启才能生效
            _restartRequired = true;
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
    /// 0: 路径, 1: 部署, 2: 模组, 3: 日志, 4: 连接, 5: 工具, 6: 主页
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

    public ObservableCollection<LanguageItem> ThemeOptions { get; } = [];

    public string SelectedTheme
    {
        get => _settingsService.Initialized ? _settingsService.Theme : "System";
        set
        {
            OnPropertyChanging();
            _settingsService.Theme = value;
            OnPropertyChanged();
        }
    }

    public bool EnableAnimations
    {
        get => _settingsService.Initialized && _settingsService.EnableAnimations;
        set
        {
            OnPropertyChanging();
            _settingsService.EnableAnimations = value;
            OnPropertyChanged();
        }
    }

    private readonly ILogger<SettingsPageViewModel> _logger;
    private readonly NavigationStore _navStore;
    private readonly SettingsService _settingsService;
    private readonly INexusModsService _nexusModsService;
    private readonly ModHashService _modHashService;
    private readonly ModService _modService;
    private readonly LocalizationService _localizationService;
    private readonly BrowserExtensionService _browserExtensionService;
    private readonly ThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IBackgroundTaskRunner _backgroundTaskRunner;
    private Task<OperationResult>? _initializationTask;
    [ObservableProperty]
    private bool _isSymbolicLinkAdminWarningVisible;
    [ObservableProperty]
    private string _pairingCode = string.Empty;
    public bool IsBrowserExtensionPaired =>
        _settingsService.Initialized && !string.IsNullOrWhiteSpace(_settingsService.BrowserExtensionTokenHash);
    [ObservableProperty]
    private int _selectedSkip = -1;
    [ObservableProperty]
    private int _selectedOrgFolder = -1;

    public SettingsPageViewModel(
        ILogger<SettingsPageViewModel> logger,
        NavigationStore navStore,
        SettingsService settingsService,
        INexusModsService nexusModsService,
        ModHashService modHashService,
        ModService modService,
        LocalizationService localizationService,
        BrowserExtensionService browserExtensionService,
        ThemeService themeService,
        IDialogService dialogService,
        IBackgroundTaskRunner backgroundTaskRunner)
    {
        _logger = logger;
        _navStore = navStore;
        _settingsService = settingsService;
        _nexusModsService = nexusModsService;
        _modHashService = modHashService;
        _modService = modService;
        _localizationService = localizationService;
        _browserExtensionService = browserExtensionService;
        _themeService = themeService;
        _dialogService = dialogService;
        _backgroundTaskRunner = backgroundTaskRunner;
        RefreshThemeOptions();
        _localizationService.LocaleChanged += LocalizationService_LocaleChanged;

        SkipList.CollectionChanged += SkipList_CollectionChanged;
        OrganizationalFolderNames.CollectionChanged += OrgFolderNames_CollectionChanged;

        StartInitialization();
    }

    private void StartInitialization()
    {
        _initializationTask ??= _backgroundTaskRunner.RunAsync(
            _localizationService["SettingsPage.LoadingSettings"],
            async (_, cancellationToken) =>
            {
                await Init(cancellationToken);
                return OperationResult.Success();
            },
            CancellationToken.None);
    }

    protected override void OnDispose()
    {
        SkipList.CollectionChanged -= SkipList_CollectionChanged;
        OrganizationalFolderNames.CollectionChanged -= OrgFolderNames_CollectionChanged;
        _localizationService.LocaleChanged -= LocalizationService_LocaleChanged;
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

    private bool ValidateSettings([NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(GameDir))
        {
            error = _localizationService["SettingsPage.ValidateGameDirEmpty"];
            return false;
        }

        if (string.IsNullOrEmpty(StorageDir))
        {
            error = _localizationService["SettingsPage.ValidateStorageDirEmpty"];
            return false;
        }

        if (string.IsNullOrEmpty(TempDir))
        {
            error = _localizationService["SettingsPage.ValidateTempDirEmpty"];
            return false;
        }

        error = null;
        return true;
    }

    private async Task Init(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading settings...");
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["SettingsPage.LoadingSettings"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);
        try
        {
            if (!await _settingsService.InitAsync(cancellationToken: cancellationToken))
                _settingsService.InitDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            await progressDialog.CloseAsync(CancellationToken.None);
            if (await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["SettingsPage.LoadSettingsFailed"],
                    _localizationService["SettingsPage.ResetConfirm"]),
                cancellationToken))
            {
                _settingsService.InitDefault();
                Update();
            }
            return;
        }
        _logger.LogInformation("Settings loaded successfully");
        await progressDialog.CloseAsync(cancellationToken);

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
        OnPropertyChanged(nameof(EnableBrowserIntegration));
        OnPropertyChanged(nameof(AutoCleanLogs));
        OnPropertyChanged(nameof(ShowSeparator));
        OnPropertyChanged(nameof(LogRetentionDays));
        OnPropertyChanged(nameof(ExtensionHost));
        OnPropertyChanged(nameof(ExtensionPort));
        OnPropertyChanged(nameof(IsBrowserExtensionPaired));
        OnPropertyChanged(nameof(NexusApiKey));
        OnPropertyChanged(nameof(SelectedLanguageCode));
        OnPropertyChanged(nameof(AvailableLanguages));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(EnableAnimations));
        OnPropertyChanged(nameof(ThemeOptions));
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
    async Task Ok(CancellationToken cancellationToken)
    {
        if (!ValidateSettings(out var validationError))
        {
            await ShowSettingsMessageAsync(validationError, MessageDialogSeverity.Error, cancellationToken);
            return;
        }

        if (!_settingsService.Validate())
        {
            await ShowSettingsMessageAsync(
                _localizationService["SettingsPage.SettingsValid"],
                MessageDialogSeverity.Error,
                cancellationToken);
            return;
        }

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["SettingsPage.SavingSettings"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);
        try
        {
            await _settingsService.SaveAsync(cancellationToken);

            // 保存设置后立即更新运行时日志级别过滤
            App.Current.LogLevel = _settingsService.LogLevel;

            // 保存后执行日志清理
            _settingsService.CleanOldLogs();
            _themeService.Apply(_settingsService.Theme, _settingsService.EnableAnimations);

            if (_settingsService.EnableBrowserIntegration)
                _browserExtensionService.Start();
            else
                _browserExtensionService.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save settings");
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowSettingsMessageAsync(
                _localizationService.Format("SettingsPage.SaveFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                CancellationToken.None);
            return;
        }
        await progressDialog.CloseAsync(cancellationToken);

        if (_restartRequired)
        {
            // IP/端口修改后提示重启才能生效
            var restart = await _dialogService.ShowAsync(
                new DialogRequest(
                    _localizationService["MessageBox.Info"],
                    _localizationService["SettingsPage.RestartForExtChange"]),
                cancellationToken);
            if (restart)
                System.Windows.Application.Current.Shutdown();
            else
                _navStore.Navigate<DashboardPageViewModel>();
        }
        else
        {
            _navStore.Navigate<DashboardPageViewModel>();
        }
    }

    private void LocalizationService_LocaleChanged(object? sender, EventArgs e)
    {
        RefreshThemeOptions();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ThemeOptions));
        OnPropertyChanged(nameof(SymbolicLinkAdminWarning));
    }

    private void RefreshThemeOptions()
    {
        ThemeOptions.Clear();
        ThemeOptions.Add(new LanguageItem(_localizationService["SettingsPage.ThemeSystem"], "System"));
        ThemeOptions.Add(new LanguageItem(_localizationService["SettingsPage.ThemeLight"], "Light"));
        ThemeOptions.Add(new LanguageItem(_localizationService["SettingsPage.ThemeDark"], "Dark"));
    }

    [RelayCommand]
    private void GenerateBrowserPairingCode()
    {
        if (!EnableBrowserIntegration)
            return;
        PairingCode = _browserExtensionService.GeneratePairingCode();
    }

    [RelayCommand]
    private async Task UnpairBrowserExtension()
    {
        await _browserExtensionService.UnpairAsync();
        PairingCode = string.Empty;
        OnPropertyChanged(nameof(IsBrowserExtensionPaired));
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
    async Task Reset()
    {
        if (!await _dialogService.ShowAsync(
            new DialogRequest(
                _localizationService["SettingsPage.ResetTitle"],
                _localizationService["SettingsPage.ResetConfirmMsg"]),
            CancellationToken.None))
            return;

        _settingsService.Reset();
        Update();
    }

    [RelayCommand]
    async Task BrowseGame(CancellationToken cancellationToken)
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
                await ShowSettingsMessageAsync(error, MessageDialogSeverity.Error, cancellationToken);
        }
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
    async Task AddSkip(CancellationToken cancellationToken)
    {
        var value = await _dialogService.PromptAsync(
            new InputDialogRequest(
                _localizationService["SettingsPage.AddSkipTitle"],
                _localizationService["SettingsPage.AddSkipMsg"],
                MaxLength: 16),
            cancellationToken);
        if (value is null)
            return;
        if (value.Length == 16)
            SkipList.Add(value);
        else
            await ShowInfoAsync("SettingsPage.AddSkipValidation", cancellationToken);
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
    async Task AddOrgFolder(CancellationToken cancellationToken)
    {
        var value = await _dialogService.PromptAsync(
            new InputDialogRequest(
                _localizationService["SettingsPage.AddOrgFolderTitle"],
                _localizationService["SettingsPage.AddOrgFolderMsg"],
                MaxLength: 100),
            cancellationToken);
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        if (OrganizationalFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            await _dialogService.ShowMessageAsync(
                new MessageDialogRequest(
                    _localizationService["MessageBox.Info"],
                    _localizationService.Format("SettingsPage.AddOrgFolderExists", new { name })),
                cancellationToken);
            return;
        }

        OrganizationalFolderNames.Add(name);
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
    async Task ValidateNexusApiKey(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NexusApiKey))
        {
            NexusApiKeyValidationResult = _localizationService["SettingsPage.ApiKeyEmpty"];
            return;
        }

        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["SettingsPage.ValidatingApiKeyTitle"],
                _localizationService["SettingsPage.ValidatingApiKeyMsg"]),
            cancellationToken);

        try
        {
            _nexusModsService.Init(NexusApiKey);
            await _nexusModsService.GetTrendingModsAsync("helldivers2");
            NexusApiKeyValidationResult = _localizationService["SettingsPage.ApiKeyValid"];
            _logger.LogInformation("Nexus API Key validated successfully");
        }
        catch (Exception ex)
        {
            NexusApiKeyValidationResult = _localizationService.Format("SettingsPage.ApiKeyFailed", new { message = ex.Message });
            _logger.LogError(ex, "Failed to validate Nexus API Key");
        }
        finally
        {
            await progressDialog.CloseAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// 强制重新计算所有模组的文件哈希值。
    /// 适用于：用户怀疑哈希缓存数据异常、手动修改过模组文件后需要刷新等情况。
    /// 后台执行，完成后底部状态栏会显示结果摘要。
    /// </summary>
    [RelayCommand]
    async Task RecomputeAllHashes(CancellationToken cancellationToken)
    {
        var modCount = _modService.Mods.Count;
        if (modCount == 0)
        {
            await ShowInfoAsync("SettingsPage.NoModsForHash", cancellationToken);
            return;
        }

        if (!await _dialogService.ShowAsync(
            new DialogRequest(
                _localizationService["Common.WarningPrefix"],
                _localizationService.Format("SettingsPage.RecomputeHashMsg", new { count = modCount })),
            cancellationToken))
            return;

        _logger.LogInformation("User requested full hash recomputation for {Count} mods", modCount);
        await _modHashService.ForceRecomputeAllAsync(_modService.Mods);
        await ShowInfoAsync("SettingsPage.RecomputeHashStarted", cancellationToken);
    }

    [RelayCommand]
    async Task DetectGame(CancellationToken cancellationToken)
    {
        await using var progressDialog = await _dialogService.OpenProgressAsync(
            new ProgressDialogRequest(
                _localizationService["SettingsPage.DetectingGame"],
                _localizationService["SettingsPage.PleaseWait"]),
            cancellationToken);

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

            foreach (var drive in Environment.GetLogicalDrives())
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
        }).WaitAsync(cancellationToken);

        if (result && path != null)
        {
            GameDir = path;
            await progressDialog.CloseAsync(cancellationToken);
        }
        else
        {
            await progressDialog.CloseAsync(CancellationToken.None);
            await ShowInfoAsync("SettingsPage.DetectGameFailed", cancellationToken);
        }
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

    private Task ShowInfoAsync(string messageKey, CancellationToken cancellationToken) =>
        ShowSettingsMessageAsync(
            _localizationService[messageKey],
            MessageDialogSeverity.Information,
            cancellationToken);

    private Task ShowSettingsMessageAsync(
        string message,
        MessageDialogSeverity severity,
        CancellationToken cancellationToken)
    {
        var titleKey = severity switch
        {
            MessageDialogSeverity.Warning => "MessageBox.Warning",
            MessageDialogSeverity.Error => "MessageBox.Error",
            _ => "MessageBox.Info"
        };
        return _dialogService.ShowMessageAsync(
            new MessageDialogRequest(_localizationService[titleKey], message, severity),
            cancellationToken);
    }
}
