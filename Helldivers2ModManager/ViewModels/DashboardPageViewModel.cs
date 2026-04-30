using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using MessageBox = Helldivers2ModManager.Components.MessageBox;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DashboardPageViewModel : PageViewModelBase
{
    public override string Title => "Mods";

    public IEnumerable<ModViewModel> Mods { get; private set; }

    public bool IsSearchEmpty => string.IsNullOrEmpty(SearchText);

    private static readonly ProcessStartInfo s_gameStartInfo = new("steam://run/553850") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_reportStartInfo = new("https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_discordStartInfo = new("https://discord.gg/helldiversmodding") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_githubStartInfo = new("https://github.com/teutinsa/Helldivers2ModManager") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_githubForkStartInfo = new("https://github.com/TYHH100/Helldivers2ModManager") { UseShellExecute = true };
    private readonly ILogger<DashboardPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly EditModStore _editModStore;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly ProfileService _profileService;
    private ObservableCollection<ModViewModel> _mods;
    [ObservableProperty]
    private string _searchText = string.Empty;
    [ObservableProperty]
    private Visibility _imagePreviewVisibility = Visibility.Hidden;
    [ObservableProperty]
    private ImageSource? _previewImageSource;
    [ObservableProperty]
    private bool _initialized = false;
    private object? _selectedGroupItem = "无";
    public object? SelectedGroupItem
    {
        get
        {
            return _selectedGroupItem;
        }
        set
        {
            if (_selectedGroupItem != value)
            {
                _selectedGroupItem = value;
                OnPropertyChanged(nameof(SelectedGroupItem));
                OnPropertyChanged(nameof(SelectedGroup));
                
                // Enable only mods in the selected group
                var selectedGroup = SelectedGroup;
                foreach (var mod in _mods)
                {
                    if (selectedGroup == null)
                    {
                        // If no group is selected, enable only mods without a group
                        mod.Enabled = mod.Data.GroupId == null;
                    }
                    else
                    {
                        // Enable only mods in the selected group
                        mod.Enabled = mod.Data.GroupId == selectedGroup.Id;
                    }
                }
                
                UpdateView();
            }
        }
    }
    public ModGroup? SelectedGroup
    {
        get
        {
            if (_selectedGroupItem is ModGroup group)
            {
                return group;
            }
            return null;
        }
        set
        {
            if (value == null)
            {
                SelectedGroupItem = "无";
            }
            else
            {
                SelectedGroupItem = value;
            }
        }
    }
    public IReadOnlyList<ModGroup> Groups => _settingsService.Initialized ? _settingsService.Groups : [];
    public IEnumerable<object> GroupItems
    {
        get
        {
            yield return "无";
            if (_settingsService.Initialized)
            {
                foreach (var group in _settingsService.Groups)
                {
                    yield return group;
                }
            }
        }
    }

    public DashboardPageViewModel(ILogger<DashboardPageViewModel> logger, IServiceProvider provider, SettingsService settingsService, ModService modService, ProfileService profileService, EditModStore editModStore)
    {
        _logger = logger;
        _navStore = new(provider.GetRequiredService<NavigationStore>);
        _editModStore = editModStore;
        _settingsService = settingsService;
        _modService = modService;
        _profileService = profileService;
        _mods = [];

        Mods = _mods;

        if (MessageBox.IsRegistered)
            _ = Init();
        else
            MessageBox.Registered += (_, _) => _ = Init();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchText))
        {
            OnPropertyChanged(nameof(IsSearchEmpty));
            ClearSearchCommand.NotifyCanExecuteChanged();
            UpdateView();
        }
        else if (e.PropertyName == nameof(SelectedGroup))
        {
            UpdateView();
        }

        base.OnPropertyChanged(e);
    }

    private async Task SaveEnabled()
    {
        if (!_settingsService.IsReadonly)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = "保存模组配置中",
                Message = "请民主官耐心等待."
            });

            await _profileService.SaveAsync(_settingsService, _mods.Select(static vm => vm.Data));

            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
    }

    private void UpdateView()
    {
        IEnumerable<ModViewModel> filteredMods = _mods;

        if (SelectedGroup != null)
        {
            filteredMods = filteredMods.Where(vm => vm.Data.GroupId == SelectedGroup.Id);
        }

        if (!IsSearchEmpty && _settingsService.Initialized)
        {
            var searchText = SearchText.Trim();

            if (searchText.StartsWith("@"))
            {
                var tagName = searchText.Substring(1);
                if (!string.IsNullOrEmpty(tagName))
                {
                    filteredMods = filteredMods.Where(vm =>
                        vm.Tags.Any(t => t.Name.Contains(tagName, StringComparison.InvariantCultureIgnoreCase)));
                }
            }
            else
            {
                filteredMods = filteredMods.Where(vm =>
                {
                    if (_settingsService.CaseSensitiveSearch)
                        return vm.Name.Contains(searchText, StringComparison.InvariantCulture);
                    return vm.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase);
                });
            }
            Mods = filteredMods.ToArray();
        }
        else
        {
            Mods = _mods;
        }
        OnPropertyChanged(nameof(Mods));
    }

    private async Task Init()
    {
        _logger.LogInformation("Initializing dashboard...");

        _logger.LogInformation("Loading settings...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = "加载设置中",
            Message = "请民主官耐心等待.",
        });
        try
        {
            if (!await _settingsService.InitAsync(false))
                _settingsService.InitDefault(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading settings failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = $"加载设置失败!",
                Message = "是否立刻前往设置?",
                Confirm = _navStore.Value.Navigate<SettingsPageViewModel>,
            });
            return;
        }
        _logger.LogInformation("Settings loaded successfully");
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

        _logger.LogInformation("Validating settings");
        if (!_settingsService.Validate())
        {
            _logger.LogError("Settings invalid");
            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = $"设置无效!",
                Message = "是否立刻前往设置?",
                Confirm = _navStore.Value.Navigate<SettingsPageViewModel>,
            });
            return;
        }
        _logger.LogInformation("Settings valid");

        _logger.LogInformation("Loading mods...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = "加载模组中",
            Message = "请民主官耐心等待.",
        });
        ModProblem[] problems;
        try
        {
            problems = await Task.Run(() => _modService.Init(_settingsService));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading mods failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"加载模组失败!\n\n{ex}",
            });
            return;
        }
        _modService.ModAdded += ModService_ModAdded;
        _modService.ModRemoved += ModService_ModRemoved;
        if (problems.Length != 0)
            _logger.LogWarning("Loaded mods with {} problems", problems.Length);
        else
            _logger.LogInformation("Mods loaded successfully");
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());

        _logger.LogInformation("Loading profile...");
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
        {
            Title = "加载配置文件中",
            Message = "请民主官耐心等待.",
        });
        IReadOnlyList<ModData>? result;
        try
        {
            result = await _profileService.LoadAsync(_settingsService, _modService);
            result ??= _profileService.InitDefault(_modService);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading profile failed");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = $"加载配置文件失败!\n\n{ex}",
            });
            return;
        }
        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        _logger.LogInformation("Profile loaded successfully");

        _logger.LogInformation("Applying profile");
        _mods = new(result.Select(data => _modService.GetOrCreateModViewModel(data, _logger, _settingsService)).ToList());
        UpdateView();

        if (problems.Length > 0)
            ShowProblems(problems, "加载模组时出现问题:", false, true);
        Initialized = true;
        _logger.LogInformation("Initialization successful");

#if DEBUG && FALSE
		ShowProblems(Enum.GetValues<ModProblemKind>().Select(static k => new ModProblem { Directory = new DirectoryInfo(@"C:\ModStorage\Test"), Kind = k }), "Problem test:", true);
#endif
    }

    private void ShowProblems(IEnumerable<ModProblem> problems, string prefix, bool error, bool isInit = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(prefix);

        var errors = problems.Where(static p => p.IsError).ToArray();
        if (errors.Length != 0)
        {
            sb.AppendLine("错误:");
            foreach (var e in errors)
            {
                sb.Append("\t - \"");
                sb.Append(e.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = e.Kind switch
                {
                    ModProblemKind.CantParseManifest => "无法解析清单文件!",
                    ModProblemKind.UnknownManifestVersion => "未知清单版本!",
                    ModProblemKind.OutOfSupportManifest => $"不支持的清单版本!请更新.\n\t\t管理器版本 {App.Version} 不支持此版本的清单文件.",
                    ModProblemKind.Duplicate => "已添加一个具有相同 GUID 的模组。!",
                    ModProblemKind.InvalidPath => e.ExtraData is not null
                        ? $"包含路径  \"{e.ExtraData}\" 无效!"
                        : "包含路径无效!",
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        var warnings = problems.Where(static p => !p.IsError).ToArray();
        if (warnings.Length != 0)
        {
            sb.AppendLine("警告:");
            foreach (var w in warnings)
            {
                sb.Append("\t - \"");
                sb.Append(w.Directory.FullName);
                sb.AppendLine("\"");

                sb.Append("\t\t");
                string desc = w.Kind switch
                {
                    ModProblemKind.NoManifestFound => isInit
                        ? "目录中未找到清单文件!\n\t\t\t执行操作: 删除(Deleting)"
                        : "目录中未找到清单文件!\n\t\t\t执行操作: 从目录推断(Inferring from directory)",
                    ModProblemKind.EmptyOptions => "清单包含空选项! 此模组可能不会产生任何效果.",
                    ModProblemKind.EmptySubOptions => "清单包含空的子选项！此模组可能无法按预期运行.",
                    ModProblemKind.EmptyIncludes => "清单包含空的包含列表！此模组可能不会产生任何作用.",
                    ModProblemKind.InvalidImagePath => w.ExtraData is not null
                        ? $"清单图片路径 \"{w.ExtraData}\" 无效!"
                        : "清单包含无效的图片路径!",
                    ModProblemKind.EmptyImagePath => "清单包含空的图片路径​!",
                    _ => throw new NotImplementedException()
                };
                sb.AppendLine(desc);
            }
        }

        if (error)
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage
            {
                Message = sb.ToString(),
            });
        else
            WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage
            {
                Message = sb.ToString(),
            });
    }

    private void ModService_ModAdded(ModData mod)
    {
        _mods.Add(new ModViewModel(mod, _logger, _settingsService));
        SearchText = string.Empty;
        UpdateView();
    }

    private void ModService_ModRemoved(ModData mod)
    {
        var vm = _mods.FirstOrDefault((vm) => vm.Data == mod);
        if (vm is not null)
        {
            _mods.Remove(vm);
            UpdateView();
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        async Task Add(string? filePath = null)
        {
            string? selectedFile = filePath;

            if (selectedFile == null)
            {
                var dialog = new OpenFileDialog
                {
                    CheckFileExists = true,
                    CheckPathExists = true,
                    InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download"),
                    Filter = "Mod档案|*.rar;*.7z;*.zip;*.tar",
                    Multiselect = false,
                    Title = "请选择要添加的模组压缩包..."
                };

                if (!(dialog.ShowDialog() ?? false))
                    return;

                selectedFile = dialog.FileName;
            }

            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage
            {
                Title = "添加模组中",
                Message = "请民主官耐心等待."
            });
            try
            {
                var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(selectedFile));
                if (problems.Length > 0)
                {
                    var error = problems.Any(static p => p.IsError);
                    var prefix = error
                        ? "由于出现问题，模组添加失败:"
                        : "模组已添加, 但有些相关问题:";
                    ShowProblems(problems, prefix, error);
                }
                else
                    WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add mod");
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = ex.Message
                });
            }
        }

    [RelayCommand]
    void Browse()
    {
        throw new NotImplementedException();
    }

    [RelayCommand]
    void Create()
    {
        _navStore.Value.Navigate<CreatePageViewModel>();
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void ReportBug()
    {
        Process.Start(s_reportStartInfo);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task TagManagement()
    {
        await SaveEnabled();

        _navStore.Value.Navigate<TagManagementPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Settings()
    {
        await SaveEnabled();

        _navStore.Value.Navigate<SettingsPageViewModel>();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Purge()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = "无法清理模组! 因为游戏路径未设置."
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = "清理模组中",
            Message = "请民主官耐心等待."
        });

        await _modService.PurgeAsync();

        WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    async Task Deploy()
    {
        if (!_settingsService.Initialized || string.IsNullOrEmpty(_settingsService.GameDirectory))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = "无法部署模组! 因为游戏路径未设置."
            });
            return;
        }

        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = "部署模组中",
            Message = "请民主官耐心等待."
        });

        var mods = _mods.Where(static vm => vm.Enabled).ToArray();
        var guids = mods.Select(static vm => vm.Guid).ToArray();

        try
        {
            await SaveEnabled();

            await _modService.DeployAsync(guids);

            WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
            {
                Message = "部署成功."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown deployment error");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    void MoveUp(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index <= 0)
            return;
        _mods.Move(index, index - 1);
    }

    [RelayCommand]
    void MoveDown(ModViewModel modVm)
    {
        var index = _mods.IndexOf(modVm);
        if (index >= _mods.Count - 1)
            return;
        _mods.Move(index, index + 1);
    }

    [RelayCommand]
    void Remove(ModViewModel modVm)
    {
        var deleteMessage = _settingsService.DeleteToRecycleBin
            ? "模组文件将被移动到回收站。"
            : "模组文件将被永久删除，此操作不可恢复！";
        
        WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
        {
            Title = "确认删除",
            Message = $"确定要删除模组 '{modVm.Name}' 吗？\n{deleteMessage}",
            Confirm = () =>
            {
                _ = DeleteModAsync(modVm);
            }
        });
    }

    private async Task DeleteModAsync(ModViewModel modVm)
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
        {
            Title = "删除模组中",
            Message = "请民主官耐心等待."
        });

        try
        {
            await _modService.RemoveAsync(modVm.Data);
            WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unknown mod removal error");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = ex.Message
            });
        }
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Run()
    {
        Process.Start(s_gameStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Github()
    {
        Process.Start(s_githubStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void GithubFork()
    {
        Process.Start(s_githubForkStartInfo);
    }

    [RelayCommand]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "This is a command of a view model and should not be static.")]
    void Discord()
    {
        Process.Start(s_discordStartInfo);
    }

    [RelayCommand]
    void OpenFileLocation(ModViewModel modVm)
    {
        try
        {
            Process.Start(new ProcessStartInfo(modVm.Data.Directory.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file location for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"无法打开文件位置: {ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditName(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = "编辑名称",
                Message = "请输入新的模组名称：",
                MaxLength = 64,
                InitialText = modVm.Name,
                Confirm = (newName) =>
                {
                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "模组名称不能为空" });
                        return;
                    }

                    modVm.Data.UpdateManifestName(newName);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组名称已更新" });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod name for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"无法编辑模组名称: {ex.Message}"
            });
        }
    }

    [RelayCommand]
    void EditDescription(ModViewModel modVm)
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
            {
                Title = "编辑描述",
                Message = "请输入新的模组描述：",
                MaxLength = 1024,
                InitialText = modVm.Description,
                Confirm = (newDescription) =>
                {
                    modVm.Data.UpdateManifestDescription(newDescription);
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组描述已更新" });
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit mod description for mod {ModName}", modVm.Name);
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
            {
                Message = $"无法编辑模组描述: {ex.Message}"
            });
        }
    }

    [RelayCommand]
    async Task EditImage(ModViewModel modVm)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            Title = "请选择要设置的模组图片..."
        };

        if (dialog.ShowDialog() ?? false)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxProgressMessage()
            {
                Title = "更新图片中",
                Message = "请民主官耐心等待."
            });

            try
            {
                string imageFileName = Path.GetFileName(dialog.FileName);
                string destinationPath = Path.Combine(modVm.Data.Directory.FullName, imageFileName);
                await CopyFileAsync(dialog.FileName, destinationPath, true);

                modVm.Data.UpdateManifestIconPath(imageFileName);

                modVm.LoadIcon();

                await SaveEnabled();

                WeakReferenceMessenger.Default.Send(new MessageBoxHideMessage());
                WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage()
                {
                    Message = "图片更新成功."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to edit image for mod {ModName}", modVm.Name);
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage()
                {
                    Message = $"图片更新失败: {ex.Message}"
                });
            }
        }
    }

    private async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite)
    {
        using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true))
        using (var destinationStream = new FileStream(destinationPath, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
        {
            await sourceStream.CopyToAsync(destinationStream);
        }
    }

    [RelayCommand]
    void Edit(ModViewModel vm)
    {
        _editModStore.CurrentMod = vm;
        _navStore.Value.Navigate<EditPageViewModel>();
    }

    bool CanClearSearch()
    {
        return !IsSearchEmpty;
    }

    [RelayCommand(CanExecute = nameof(CanClearSearch))]
    void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    void CreateGroup()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "创建分组",
            Message = "请输入新分组的名称：",
            MaxLength = 32,
            Confirm = (groupName) =>
            {
                if (string.IsNullOrWhiteSpace(groupName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "分组名称不能为空" });
                    return;
                }

                _settingsService.Groups.Add(new ModGroup(groupName));
                if (!_settingsService.IsReadonly)
                {
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组创建成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法创建分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void DeleteGroup(ModGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;

        var modsInGroup = _mods.Where(vm => vm.Data.GroupId == group.Id).ToArray();

        if (!_settingsService.IsReadonly)
        {
            var message = modsInGroup.Length > 0
                ? $"确定要删除分组 '{group.Name}' 吗？此操作将清除 {modsInGroup.Length} 个模组的分组信息，且不可恢复。"
                : $"确定要删除分组 '{group.Name}' 吗？此操作不可恢复。";

            WeakReferenceMessenger.Default.Send(new MessageBoxConfirmMessage
            {
                Title = "确认删除",
                Message = message,
                Confirm = () =>
                {
                    foreach (var mod in modsInGroup)
                    {
                        mod.Data.GroupId = null;
                    }

                    _settingsService.Groups.Remove(group);
                    if (SelectedGroup == group)
                    {
                        SelectedGroup = null;
                    }
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组删除成功" });
                }
            });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法删除分组，设置处于只读模式" });
        }
    }

    [RelayCommand]
    void RenameGroup(ModGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null)
            return;

        WeakReferenceMessenger.Default.Send(new MessageBoxInputMessage
        {
            Title = "重命名分组",
            Message = "请输入新的分组名称：",
            MaxLength = 32,
            InitialText = group.Name,
            Confirm = (newName) =>
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "分组名称不能为空" });
                    return;
                }

                if (!_settingsService.IsReadonly)
                {
                    group.Name = newName;
                    _ = _settingsService.SaveAsync();
                    OnPropertyChanged(nameof(GroupItems));
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "分组重命名成功" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法重命名分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void SetGroup(ModViewModel modVm)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        // Create a list of group options including "None"
        var groupOptions = new List<object> { "无" };
        groupOptions.AddRange(_settingsService.Groups);

        // Show a selection dialog
        WeakReferenceMessenger.Default.Send(new MessageBoxSelectionMessage
        {
            Title = "设置分组",
            Message = "请为模组选择一个分组：",
            Options = groupOptions,
            Confirm = (selectedOption) =>
            {
                if (!_settingsService.IsReadonly)
                {
                    if (selectedOption.ToString() == "无")
                    {
                        modVm.Data.GroupId = null;
                    }
                    else if (selectedOption is ModGroup selectedGroup)
                    {
                        modVm.Data.GroupId = selectedGroup.Id;
                    }

                    _ = SaveEnabled();
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组分组已更新" });
                }
                else
                {
                    WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法设置分组，设置处于只读模式" });
                }
            }
        });
    }

    [RelayCommand]
    void ApplyAll()
    {
    }

    [RelayCommand]
    void ShowImagePreview(ImageSource imageSource)
    {
        PreviewImageSource = imageSource;
        ImagePreviewVisibility = Visibility.Visible;
    }

    [RelayCommand]
    void HideImagePreview()
    {
        ImagePreviewVisibility = Visibility.Hidden;
        PreviewImageSource = null;
    }

    [RelayCommand]
    void EditModTags(ModViewModel modVm)
    {
        if (modVm == null || !_settingsService.Initialized)
            return;

        var selectedTagIds = modVm.Data.TagIds.ToList();
        var selectableTags = _settingsService.Tags.Select(t => new TagSelectionItem(t, selectedTagIds.Contains(t.Id))).ToList();

        WeakReferenceMessenger.Default.Send(new MessageBoxTagSelectionMessage
            {
                Title = "设置标签",
                Message = "请选择模组的标签：",
                Tags = selectableTags,
                Confirm = (selectedTags) =>
                {
                    if (!_settingsService.IsReadonly)
                    {
                        modVm.Data.TagIds = selectedTags.Select(t => t.Tag.Id).ToList();
                        _ = SaveEnabled();
                        WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = "模组标签已更新" });
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法设置标签，设置处于只读模式" });
                    }
                }
            });
    }

    public IReadOnlyList<ModTag> AllTags => _settingsService.Initialized ? _settingsService.Tags : [];
    public IEnumerable<object> TagItems => _settingsService.Initialized ? _settingsService.Tags : [];

    protected override void OnDispose()
    {
        _modService.ModAdded -= ModService_ModAdded;
        _modService.ModRemoved -= ModService_ModRemoved;
        _mods.Clear();
    }
}