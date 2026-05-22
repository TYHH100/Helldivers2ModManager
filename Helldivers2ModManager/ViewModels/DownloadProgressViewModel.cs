using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class DownloadProgressViewModel : PageViewModelBase
{
    private readonly ILogger<DownloadProgressViewModel> _logger;
    private readonly BrowserExtensionService _browserExtensionService;
    private readonly Lazy<NavigationStore> _navStore;

    public override string Title => "下载进度";

    public ObservableCollection<DownloadTask> DownloadTasks => _browserExtensionService.DownloadTasks;

    public DownloadProgressViewModel(
        ILogger<DownloadProgressViewModel> logger,
        IServiceProvider provider,
        BrowserExtensionService browserExtensionService)
    {
        _logger = logger;
        _browserExtensionService = browserExtensionService;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);

        _browserExtensionService.DownloadStarted += OnDownloadStarted;
        _browserExtensionService.DownloadProgressChanged += OnDownloadProgressChanged;
        _browserExtensionService.DownloadCompleted += OnDownloadCompleted;
        _browserExtensionService.DownloadFailed += OnDownloadFailed;
    }

    [RelayCommand]
    private void GoBack()
    {
        _navStore.Value.Navigate<DashboardPageViewModel>();
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
    }

    private void OnDownloadFailed(DownloadTask task)
    {
        _logger.LogError("Download failed: {Filename} - {Error}", task.Filename, task.ErrorMessage);
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