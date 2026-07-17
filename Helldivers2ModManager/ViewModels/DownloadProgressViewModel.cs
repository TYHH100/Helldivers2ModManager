using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.Core.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DownloadProgressViewModel : PageViewModelBase
{
    private readonly ILogger<DownloadProgressViewModel> _logger;
    private readonly BrowserExtensionService _browserExtensionService;
    private readonly SettingsService _settingsService;
    private readonly INavigationService _navigationService;
    private readonly LocalizationService _localizationService;
    private readonly IDialogService _dialogService;
    private readonly IClipboardService _clipboardService;

    public override string Title => _localizationService["DashboardPage.DownloadProgress"];

    public ObservableCollection<DownloadTask> DownloadTasks => _browserExtensionService.DownloadTasks;

    /// <summary>
    /// 是否存在已完成/失败/取消的任务（用于"清除已完成"按钮的可用性）
    /// </summary>
    [ObservableProperty]
    private bool _hasCompletedTasks;

    /// <summary>
    /// 手动输入的下载链接
    /// </summary>
    [ObservableProperty]
    private string _manualUrl = string.Empty;

    public DownloadProgressViewModel(
        ILogger<DownloadProgressViewModel> logger,
        INavigationService navigationService,
        BrowserExtensionService browserExtensionService,
        SettingsService settingsService,
        LocalizationService localizationService,
        IDialogService dialogService,
        IClipboardService clipboardService)
    {
        _logger = logger;
        _browserExtensionService = browserExtensionService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _clipboardService = clipboardService;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };

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
        _navigationService.Navigate(typeof(DashboardPageViewModel), root: true);
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
    private async Task CopyDownloadUrl(DownloadTask task, CancellationToken cancellationToken)
    {
        try
        {
            await _clipboardService.SetTextAsync(task.Url, cancellationToken);
            _logger.LogDebug("Copied download URL to clipboard: {Url}", task.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to copy URL to clipboard");
        }
    }

    /// <summary>
    /// 手动添加下载链接
    /// </summary>
    [RelayCommand]
    private async Task AddManualDownloadAsync()
    {
        var url = ManualUrl.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.LogWarning("User attempted to add empty download URL");
            return;
        }

        var success = _browserExtensionService.AddManualDownload(url);

        if (success)
        {
            _logger.LogInformation("Manual download added successfully: {Url}", url);
            ManualUrl = string.Empty; // 清空输入框
        }
        else
        {
            _logger.LogWarning("Failed to add manual download: {Url}", url);
            await _dialogService.ShowMessageAsync(
                new MessageDialogRequest(
                    _localizationService["DownloadProgress.AddFailedTitle"],
                    _localizationService["DownloadProgress.AddFailed"],
                    MessageDialogSeverity.Warning),
                CancellationToken.None);
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
