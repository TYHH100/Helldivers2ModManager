using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public string Title => $"{_localizationService["Common.AppName"]} {Version} - {CurrentViewModel.Title}";

    public PageViewModelBase CurrentViewModel => _navigationStore.CurrentViewModel;

    public Brush Background => _background;

    public string Version => string.IsNullOrEmpty(App.VersionAddition) ? $"v{App.Version}" : $"v{App.Version} {App.VersionAddition}";

    private static readonly ProcessStartInfo s_helpStartInfo = new(@"https://teutinsa.github.io/hd2mm-site/index.html") { UseShellExecute = true };
    private static readonly ProcessStartInfo s_reportBugStartInfo = new(@"https://github.com/TYHH100/Helldivers2ModManager/issues") { UseShellExecute = true };
    private readonly NavigationStore _navigationStore;
    private readonly SolidColorBrush _background;
    private readonly LocalizationService _localizationService;
    private readonly DatabaseService _databaseService;

    public bool IsDatabaseReadOnly => _databaseService.IsReadOnly;

    public string DatabaseReadOnlyMessage => _localizationService["Database.ReadOnlyMode"];

    internal string UiTestCurrentLocale => _localizationService.CurrentLocale;

    internal int UiTestBusinessServiceIdentity => RuntimeHelpers.GetHashCode(_databaseService);
    private bool _disposed;

    public MainViewModel(NavigationStore navigationStore, LocalizationService localizationService, DatabaseService databaseService)
    {
        _navigationStore = navigationStore;
        _localizationService = localizationService;
        _databaseService = databaseService;
        _background = new SolidColorBrush(Color.FromScRgb(0.7f, 0, 0, 0));

        _navigationStore.Navigated += NavigationStore_Navigated;
        _databaseService.ReadOnlyModeChanged += DatabaseService_ReadOnlyModeChanged;
        _localizationService.LocaleChanged += LocalizationService_LocaleChanged;
    }

    private void DatabaseService_ReadOnlyModeChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(IsDatabaseReadOnly));

    private void LocalizationService_LocaleChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DatabaseReadOnlyMessage));
    }

    internal void SwitchLocaleForUiTest(string locale) => _localizationService.SelectedLanguage = locale;

    internal void NavigateTagManagementForUiTest() => _navigationStore.NavigateRoot<TagManagementPageViewModel>();

    internal void NavigateDeploymentOrderForUiTest() => _navigationStore.NavigateRoot<DeploymentOrderPageViewModel>();

    internal bool TryNavigateManifestForUiTest() =>
        _navigationStore.CurrentViewModel is DashboardPageViewModel dashboard &&
        dashboard.TryOpenFirstManifestForUiTest();

    private void NavigationStore_Navigated(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentViewModel));
        OnPropertyChanged(nameof(Title));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NavigateHome() => _navigationStore.NavigateRoot<DashboardPageViewModel>();

    [RelayCommand]
    private void NavigateCreate() => _navigationStore.NavigateRoot<CreatePageViewModel>();

    [RelayCommand]
    private void NavigateDownloads() => _navigationStore.NavigateRoot<DownloadProgressViewModel>();

    [RelayCommand]
    private void NavigateBackgroundTasks() => _navigationStore.NavigateRoot<BackgroundTasksPageViewModel>();

    [RelayCommand]
    private void NavigateSettings() => _navigationStore.NavigateRoot<SettingsPageViewModel>();

    [RelayCommand]
    private void NavigateHelp() => _navigationStore.NavigateRoot<HelpPageViewModel>();

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _navigationStore.GoBack();

    private bool CanGoBack() => _navigationStore.CanGoBack;

    [RelayCommand]
    void Help()
    {
        Process.Start(s_helpStartInfo);
    }

    [RelayCommand]
    void ReportBug()
    {
        Process.Start(s_reportBugStartInfo);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _navigationStore.Navigated -= NavigationStore_Navigated;
            _databaseService.ReadOnlyModeChanged -= DatabaseService_ReadOnlyModeChanged;
            _localizationService.LocaleChanged -= LocalizationService_LocaleChanged;
        }

        _disposed = true;
    }
}
