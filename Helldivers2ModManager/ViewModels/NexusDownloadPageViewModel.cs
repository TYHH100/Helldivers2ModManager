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
    public override string Title => _localizationService["NexusDownloadPage.Title"];

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
    private readonly LocalizationService _localizationService;

    public NexusDownloadPageViewModel(
        ILogger<NexusDownloadPageViewModel> logger,
        IServiceProvider provider,
        INexusModsService nexusModsService,
        ModService modService,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        _logger = logger;
        _navStore = new Lazy<NavigationStore>(provider.GetRequiredService<NavigationStore>);
        _nexusModsService = nexusModsService;
        _modService = modService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _localizationService.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task FetchMod()
        {
            if (string.IsNullOrWhiteSpace(NexusUrl))
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.EnterUrl"] });
                return;
            }

            var parsed = ParseNexusUrl(NexusUrl);
            if (!parsed.HasValue)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.ParseFailed"] });
                return;
            }

            var (gameDomain, modId) = parsed.Value;

            if (!_nexusModsService.Initialized && !string.IsNullOrEmpty(_settingsService.NexusApiKey))
            {
                _nexusModsService.Init(_settingsService.NexusApiKey);
            }

            if (!_nexusModsService.Initialized)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.NoApiKey"] });
                return;
            }

            IsLoading = true;
            StatusMessage = _localizationService["NexusDownloadPage.Fetching"];

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
                    StatusMessage = _localizationService["NexusDownloadPage.NoFiles"];
                }
                else
                {
                    SelectedFile = ModFiles.FirstOrDefault(f => f.IsPrimary == true) ?? ModFiles.FirstOrDefault();
                    StatusMessage = $"{_localizationService["NexusDownloadPage.FoundPrefix"]}{ModFiles.Count}{_localizationService["MessageBox.NeedUpdateSuffix"]}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch mod from Nexus");
                StatusMessage = $"{_localizationService["NexusDownloadPage.FetchFailedPrefix"]}{ex.Message}";
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"{_localizationService["NexusDownloadPage.FetchError"]}{ex.Message}" });
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
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.SelectFile"] });
                return;
            }

            if (!_modService.Initialized || !_settingsService.Initialized)
            {
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.ServiceNotReady"] });
                return;
            }

            IsDownloading = true;
            StatusMessage = _localizationService["NexusDownloadPage.Downloading"];
            string? downloadedPath = null;

            try
            {
                var fileName = !string.IsNullOrEmpty(SelectedFile.Name) 
                    ? SelectedFile.Name 
                    : $"{SelectedMod.Name}.zip";
                var tempPath = Path.Combine(_settingsService.TempDirectory, fileName);
                
                var parsed = ParseNexusUrl(NexusUrl);
                if (!parsed.HasValue)
                {
                    throw new InvalidOperationException(_localizationService["NexusDownloadPage.ParseFailed"]);
                }
                
                downloadedPath = await _nexusModsService.DownloadModFileAsync(
                    parsed.Value.GameDomain,
                    SelectedMod.GameScopedId,
                    SelectedFile.GameScopedId,
                    tempPath);

                StatusMessage = _localizationService["NexusDownloadPage.Importing"];
                
                var problems = await _modService.TryAddModFromArchiveAsync(new FileInfo(downloadedPath));
                
                if (problems.Length > 0)
                {
                    var hasError = problems.Any(p => p.IsError);
                    var prefix = hasError
                        ? _localizationService["NexusDownloadPage.ImportProblems"]
                        : _localizationService["NexusDownloadPage.ImportWarnings"];
                    
                    ShowProblems(problems, prefix, hasError);
                }
                else
                {
                    StatusMessage = _localizationService["NexusDownloadPage.ImportSuccess"];
                    WeakReferenceMessenger.Default.Send(new MessageBoxInfoMessage { Message = $"{_localizationService["NexusDownloadPage.ImportSuccessPrefix"]}{SelectedMod.Name}{_localizationService["NexusDownloadPage.ImportSuccessSuffix"]}" });
                    _navStore.Value.Navigate<DashboardPageViewModel>();
                }
            }
            catch (NexusPremiumRequiredException)
            {
                StatusMessage = _localizationService["NexusDownloadPage.PremiumRequired"];
                ShowPremiumRequiredMessage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download or import mod");
                StatusMessage = $"{_localizationService["NexusDownloadPage.DownloadFailedPrefix"]}{ex.Message}";
                WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"{_localizationService["NexusDownloadPage.OperationFailed"]}{ex.Message}" });
            }
            finally
            {
                if (!string.IsNullOrEmpty(downloadedPath) && File.Exists(downloadedPath))
                {
                    try
                    {
                        File.Delete(downloadedPath);
                        _logger.LogInformation("Cleaned up temporary download file: {Path}", downloadedPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete temporary download file: {Path}", downloadedPath);
                    }
                }
                IsDownloading = false;
            }
        }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (string.IsNullOrEmpty(NexusUrl))
        {
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.EnterLinkFirst"] });
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
            WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = $"{_localizationService["NexusDownloadPage.OpenBrowserFailed"]}{ex.Message}" });
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navStore.Value.Navigate<DashboardPageViewModel>();
    }

    private void ShowPremiumRequiredMessage()
    {
        WeakReferenceMessenger.Default.Send(new MessageBoxErrorMessage { Message = _localizationService["NexusDownloadPage.PremiumMsg"] });
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
            sb.AppendLine(_localizationService["Common.ErrorPrefix"]);
            foreach (var p in errors)
            {
                sb.Append("\t- \"");
                sb.Append(p.Directory?.Name ?? _localizationService["Converters.Unknown"]);
                sb.AppendLine("\"");
            }
        }

        var warnings = problems.Where(static p => !p.IsError).ToArray();
        if (warnings.Length != 0)
        {
            sb.AppendLine(_localizationService["Common.WarningPrefix"]);
            foreach (var p in warnings)
            {
                sb.Append("\t- \"");
                sb.Append(p.Directory?.Name ?? _localizationService["Converters.Unknown"]);
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
