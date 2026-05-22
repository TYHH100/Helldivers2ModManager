using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Helldivers2ModManager.Components;
using Helldivers2ModManager.Exceptions.Nexus;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class NexusDownloadPageViewModel : PageViewModelBase
{
    public override string Title => "从 Nexus 下载模组";

    [ObservableProperty]
    private string _nexusUrl = string.Empty;

    [ObservableProperty]
    private Mod? _selectedMod;

    [ObservableProperty]
    private ModFile? _selectedFile;

    [ObservableProperty]
    private List<ModFile>? _modFiles;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public bool CanDownload => SelectedFile != null && SelectedMod != null;

    public string ModPageUrl => !string.IsNullOrEmpty(NexusUrl) && SelectedMod != null 
        ? NexusUrl 
        : string.Empty;

    private readonly ILogger<NexusDownloadPageViewModel> _logger;
    private readonly Lazy<NavigationStore> _navStore;
    private readonly INexusModsService _nexusModsService;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;

    public NexusDownloadPageViewModel(
        ILogger<NexusDownloadPageViewModel> logger,
        IServiceProvider provider,
        INexusModsService nexusModsService,
        ModService modService,
        SettingsService settingsService)
    {
        _logger = logger;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _nexusModsService = nexusModsService;
        _modService = modService;
        _settingsService = settingsService;
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task FetchMod()
        {
            if (string.IsNullOrWhiteSpace(NexusUrl))
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "请输入 Nexus Mods 链接" });
                return;
            }

            var parsed = ParseNexusUrl(NexusUrl);
            if (!parsed.HasValue)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "无法解析链接，请确保链接格式正确\n示例: https://www.nexusmods.com/helldivers2/mods/123" });
                return;
            }

            var (gameDomain, modId) = parsed.Value;

            if (!_nexusModsService.Initialized && !string.IsNullOrEmpty(_settingsService.NexusApiKey))
            {
                _nexusModsService.Init(_settingsService.NexusApiKey);
            }

            if (!_nexusModsService.Initialized)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "请先在设置中配置 Nexus API Key" });
                return;
            }

            IsLoading = true;
            StatusMessage = "正在获取模组信息...";

            try
            {
                SelectedMod = await _nexusModsService.GetModAsync(gameDomain, modId);
                ModFiles = await _nexusModsService.GetModFilesAsync(gameDomain, modId);

                // 处理缺失的字段，给文件一个友好的默认名称
                if (SelectedMod != null)
                {
                    foreach (var file in ModFiles)
                    {
                        if (string.IsNullOrEmpty(file.Name))
                        {
                            file.Name = !string.IsNullOrEmpty(file.Version) 
                                ? $"{SelectedMod.Name} v{file.Version}" 
                                : SelectedMod.Name;
                        }
                    }
                }

                if (ModFiles.Count == 0)
                {
                    StatusMessage = "没有找到可用的下载文件";
                }
                else
                {
                    SelectedFile = ModFiles.FirstOrDefault(f => f.IsPrimary == true) ?? ModFiles.FirstOrDefault();
                    StatusMessage = $"找到 {ModFiles.Count} 个文件";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch mod from Nexus");
                StatusMessage = $"获取失败: {ex.Message}";
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"获取模组信息失败: {ex.Message}" });
            }
            finally
            {
                IsLoading = false;
            }
        }

    [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task DownloadAndImport()
        {
            if (SelectedFile == null || SelectedMod == null)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "请先选择一个文件" });
                return;
            }

            if (!_modService.Initialized || !_settingsService.Initialized)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "模组服务未初始化" });
                return;
            }

            IsDownloading = true;
            StatusMessage = "正在下载模组...";

            try
            {
                var fileName = !string.IsNullOrEmpty(SelectedFile.Name) 
                    ? SelectedFile.Name 
                    : $"{SelectedMod.Name}.zip";
                var tempPath = Path.Combine(_settingsService.TempDirectory, fileName);
                
                var parsed = ParseNexusUrl(NexusUrl);
                if (!parsed.HasValue)
                {
                    throw new InvalidOperationException("无法解析 URL");
                }
                
                var downloadedPath = await _nexusModsService.DownloadModFileAsync(
                    parsed.Value.GameDomain,
                    SelectedMod.GameScopedId,
                    SelectedFile.GameScopedId,
                    tempPath);

                StatusMessage = "正在导入模组...";
                
                var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(downloadedPath));
                
                if (problems.Length > 0)
                {
                    var hasError = problems.Any(p => p.IsError);
                    var prefix = hasError
                        ? "导入过程中出现问题:"
                        : "模组已导入，但有一些提示:";
                    
                    ShowProblems(problems, prefix, hasError);
                }
                else
                {
                    StatusMessage = "导入成功！";
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = $"模组 '{SelectedMod.Name}' 已成功导入" });
                    _navStore.Value.Navigate<DashboardPageViewModel>();
                }
            }
            catch (NexusPremiumRequiredException)
            {
                StatusMessage = "下载需要 Premium";
                ShowPremiumRequiredMessage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download or import mod");
                StatusMessage = $"下载/导入失败: {ex.Message}";
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"操作失败: {ex.Message}" });
            }
            finally
            {
                IsDownloading = false;
            }
        }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(NexusUrl))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = "请先输入 Nexus Mods 链接" });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = NexusUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Nexus page in browser");
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"无法打开浏览器: {ex.Message}" });
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navStore.Value.Navigate<DashboardPageViewModel>();
    }

    private void ShowPremiumRequiredMessage()
    {
        var message = @"下载功能需要 Nexus Mods Premium 会员资格。

您可以：
1. 升级为 Nexus Mods Premium 会员
2. 点击 ""在浏览器中打开"" 按钮手动下载模组
3. 下载后使用 ""导入本地模组"" 功能添加到管理器";

        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = message });
    }

    private (string GameDomain, string ModId)? ParseNexusUrl(string url)
    {
        var pattern = @"nexusmods\.com/([^/]+)/mods/(\d+)";
        var match = Regex.Match(url, pattern);
        
        if (match.Success && match.Groups.Count >= 3)
        {
            return (match.Groups[1].Value, match.Groups[2].Value);
        }
        
        return null;
    }

    private void ShowProblems(IEnumerable<ModProblem> problems, string prefix, bool error)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(prefix);

        var errors = problems.Where(static p => p.IsError).ToArray();
        if (errors.Length != 0)
        {
            sb.AppendLine("错误:");
            foreach (var p in errors)
            {
                sb.Append("\t- \"");
                sb.Append(p.Directory?.Name ?? "未知");
                sb.AppendLine("\"");
            }
        }

        var warnings = problems.Where(static p => !p.IsError).ToArray();
        if (warnings.Length != 0)
        {
            sb.AppendLine("警告:");
            foreach (var p in warnings)
            {
                sb.Append("\t- \"");
                sb.Append(p.Directory?.Name ?? "未知");
                sb.AppendLine("\"");
            }
        }

        if (error)
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = sb.ToString() });
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxWarningMessage { Message = sb.ToString() });
        }
    }
}
