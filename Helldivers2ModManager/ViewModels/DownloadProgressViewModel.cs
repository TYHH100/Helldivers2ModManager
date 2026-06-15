using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DownloadProgressViewModel : PageViewModelBase
{
    private readonly ILogger<DownloadProgressViewModel> _logger;
    private readonly BrowserExtensionService _browserExtensionService;
    private readonly SettingsService _settingsService;
    private readonly Lazy<NavigationStore> _navStore;

    public override string Title => "下载进度";

    public ObservableCollection<DownloadTask> DownloadTasks => _browserExtensionService.DownloadTasks;

    /// <summary>
    /// 是否存在已完成/失败/取消的任务（用于"清除已完成"按钮的可用性）
    /// </summary>
    [ObservableProperty]
    private bool _hasCompletedTasks;

    public DownloadProgressViewModel(
        ILogger<DownloadProgressViewModel> logger,
        IServiceProvider provider,
        BrowserExtensionService browserExtensionService,
        SettingsService settingsService)
    {
        _logger = logger;
        _browserExtensionService = browserExtensionService;
        _settingsService = settingsService;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);

        _browserExtensionService.DownloadStarted += OnDownloadStarted;
        _browserExtensionService.DownloadProgressChanged += OnDownloadProgressChanged;
        _browserExtensionService.DownloadCompleted += OnDownloadCompleted;
        _browserExtensionService.DownloadFailed += OnDownloadFailed;

        // 初始检查是否有已完成任务
        UpdateHasCompletedTasks();

        // 监听集合变化以更新按钮状态
        DownloadTasks.CollectionChanged += (_, _) => UpdateHasCompletedTasks();
    }

    [RelayCommand]
    private void GoBack()
    {
        _navStore.Value.Navigate<DashboardPageViewModel>();
    }

    /// <summary>
    /// 取消正在进行的下载任务
    /// </summary>
    [RelayCommand]
    private void CancelDownload(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Pending)
        {
            _browserExtensionService.CancelDownload(task.Id);
            _logger.LogInformation("User cancelled download: {Filename}", task.Filename);
        }
    }

    /// <summary>
    /// 重试失败或取消的下载任务
    /// </summary>
    [RelayCommand]
    private async Task RetryDownload(DownloadTask task)
    {
        if (task.Status == DownloadStatus.Failed || task.Status == DownloadStatus.Cancelled)
        {
            _logger.LogInformation("User retrying download: {Filename}", task.Filename);
            await _browserExtensionService.RetryDownloadAsync(task);
        }
    }

    /// <summary>
    /// 移除非下载中的任务
    /// </summary>
    [RelayCommand]
    private void RemoveTask(DownloadTask task)
    {
        _browserExtensionService.RemoveDownloadTask(task);
        _logger.LogInformation("User removed download task: {Filename}", task.Filename);
    }

    /// <summary>
    /// 打开已完成下载的文件所在位置
    /// </summary>
    [RelayCommand]
    private void OpenFileLocation(DownloadTask task)
    {
        if (task.Status != DownloadStatus.Completed)
            return;

        var tempPath = Path.Combine(_settingsService.TempDirectory, task.Filename);
        if (!File.Exists(tempPath))
        {
            _logger.LogWarning("Downloaded file not found: {Path}", tempPath);
            return;
        }

        try
        {
            // 使用资源管理器打开并选中文件
            Process.Start("explorer.exe", $"/select,\"{tempPath}\"");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file location: {Path}", tempPath);
        }
    }

    /// <summary>
    /// 清除所有已完成、失败或取消的下载任务
    /// </summary>
    [RelayCommand]
    private void ClearCompleted()
    {
        _browserExtensionService.ClearCompletedTasks();
        _logger.LogInformation("User cleared completed download tasks");
    }

    /// <summary>
    /// 复制下载链接到剪贴板
    /// </summary>
    [RelayCommand]
    private void CopyDownloadUrl(DownloadTask task)
    {
        try
        {
            Clipboard.SetText(task.Url);
            _logger.LogDebug("Copied download URL to clipboard: {Url}", task.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy URL to clipboard");
        }
    }

    private void UpdateHasCompletedTasks()
    {
        HasCompletedTasks = DownloadTasks.Any(t =>
            t.Status == DownloadStatus.Completed ||
            t.Status == DownloadStatus.Failed ||
            t.Status == DownloadStatus.Cancelled);
    }

    private void OnDownloadStarted(DownloadTask task)
    {
        _logger.LogInformation("Download started: {Filename}", task.Filename);
    }

    private void OnDownloadProgressChanged(DownloadTask task)
    {
        _logger.LogDebug("Download progress: {Filename} - {Progress:P}", task.Filename, task.Progress);
    }

    private void OnDownloadCompleted(DownloadTask task)
    {
        _logger.LogInformation("Download completed: {Filename}", task.Filename);
        UpdateHasCompletedTasks();
    }

    private void OnDownloadFailed(DownloadTask task)
    {
        _logger.LogError("Download failed: {Filename} - {Error}", task.Filename, task.ErrorMessage);
        UpdateHasCompletedTasks();
    }

    protected override void OnDispose()
    {
        _browserExtensionService.DownloadStarted -= OnDownloadStarted;
        _browserExtensionService.DownloadProgressChanged -= OnDownloadProgressChanged;
        _browserExtensionService.DownloadCompleted -= OnDownloadCompleted;
        _browserExtensionService.DownloadFailed -= OnDownloadFailed;
        
        base.OnDispose();
    }
}
