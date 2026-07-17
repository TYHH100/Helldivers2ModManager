using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Exceptions.Nexus;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Models.Nexus;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.Stores;
using Helldivers2ModManager.Core.UI;
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
    private readonly INavigationService _navigationService;
    private readonly INexusModsService _nexusModsService;
    private readonly ModService _modService;
    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly IDialogService _dialogService;

    public NexusDownloadPageViewModel(
        ILogger<NexusDownloadPageViewModel> logger,
        INavigationService navigationService,
        INexusModsService nexusModsService,
        ModService modService,
        SettingsService settingsService,
        LocalizationService localizationService,
        IDialogService dialogService)
    {
        _logger = logger;
        _navigationService = navigationService;
        _nexusModsService = nexusModsService;
        _modService = modService;
        _settingsService = settingsService;
        _localizationService = localizationService;
        _dialogService = dialogService;
        _localizationService.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task FetchMod(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(NexusUrl))
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.EnterUrl"], MessageDialogSeverity.Error, cancellationToken);
            return;
        }

        var parsed = ParseNexusUrl(NexusUrl);
        if (!parsed.HasValue)
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.ParseFailed"], MessageDialogSeverity.Error, cancellationToken);
            return;
        }

        var (gameDomain, modId) = parsed.Value;

        if (!_nexusModsService.Initialized && !string.IsNullOrEmpty(_settingsService.NexusApiKey))
        {
            _nexusModsService.Init(_settingsService.NexusApiKey);
        }

        if (!_nexusModsService.Initialized)
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.NoApiKey"], MessageDialogSeverity.Error, cancellationToken);
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
                StatusMessage = _localizationService.Format("NexusDownloadPage.Found", new { count = ModFiles.Count });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch mod from Nexus");
            StatusMessage = _localizationService.Format("NexusDownloadPage.FetchFailed", new { message = ex.Message });
            await ShowMessageAsync(
                _localizationService.Format("NexusDownloadPage.FetchError", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task DownloadAndImport(CancellationToken cancellationToken)
    {
        if (SelectedFile == null || SelectedMod == null)
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.SelectFile"], MessageDialogSeverity.Error, cancellationToken);
            return;
        }

        if (!_modService.Initialized || !_settingsService.Initialized)
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.ServiceNotReady"], MessageDialogSeverity.Error, cancellationToken);
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

                await ShowProblemsAsync(problems, prefix, hasError, cancellationToken);
            }
            else
            {
                StatusMessage = _localizationService["NexusDownloadPage.ImportSuccess"];
                await ShowMessageAsync(
                    _localizationService.Format("NexusDownloadPage.ImportSuccessMessage", new { modName = SelectedMod.Name }),
                    MessageDialogSeverity.Information,
                    cancellationToken);
                _navigationService.Navigate(typeof(DashboardPageViewModel), root: true);
            }
        }
        catch (NexusPremiumRequiredException)
        {
            StatusMessage = _localizationService["NexusDownloadPage.PremiumRequired"];
            await ShowPremiumRequiredMessageAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download or import mod");
            StatusMessage = _localizationService.Format("NexusDownloadPage.DownloadFailed", new { message = ex.Message });
            await ShowMessageAsync(
                _localizationService.Format("NexusDownloadPage.OperationFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
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
    private async Task OpenInBrowser(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(NexusUrl))
        {
            await ShowMessageAsync(_localizationService["NexusDownloadPage.EnterLinkFirst"], MessageDialogSeverity.Error, cancellationToken);
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
            await ShowMessageAsync(
                _localizationService.Format("NexusDownloadPage.OpenBrowserFailed", new { message = ex.Message }),
                MessageDialogSeverity.Error,
                cancellationToken);
        }
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.Navigate(typeof(DashboardPageViewModel), root: true);
    }

    private Task ShowPremiumRequiredMessageAsync(CancellationToken cancellationToken)
    {
        return ShowMessageAsync(
            _localizationService["NexusDownloadPage.PremiumRequiredMsg"],
            MessageDialogSeverity.Error,
            cancellationToken);
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

    private Task ShowProblemsAsync(IEnumerable<ModProblem> problems, string prefix, bool error, CancellationToken cancellationToken)
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

        return ShowMessageAsync(
            sb.ToString(),
            error ? MessageDialogSeverity.Error : MessageDialogSeverity.Warning,
            cancellationToken);
    }

    private Task ShowMessageAsync(string message, MessageDialogSeverity severity, CancellationToken cancellationToken)
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
