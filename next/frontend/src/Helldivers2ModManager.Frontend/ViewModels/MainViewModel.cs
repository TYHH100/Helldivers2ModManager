using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Navigation;

namespace Helldivers2ModManager.Frontend.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    public NavigationItem(string routeKey, string group, string title)
    {
        RouteKey = routeKey;
        Group = group;
        Title = title;
    }

    public string RouteKey { get; }

    public string Group { get; }

    public string Title { get; }

    public bool IsCurrent { get => _isCurrent; private set => SetProperty(ref _isCurrent, value); }

    private bool _isCurrent;

    internal void SetCurrent(bool isCurrent) => IsCurrent = isCurrent;
}

public sealed class NavigationModule : ObservableObject
{
    public NavigationModule(string key, string title, IReadOnlyList<NavigationItem> pages)
    {
        Key = key;
        Title = title;
        Pages = pages;
    }

    public string Key { get; }

    public string Title { get; }

    public IReadOnlyList<NavigationItem> Pages { get; }

    public bool IsCurrent { get => _isCurrent; private set => SetProperty(ref _isCurrent, value); }

    private bool _isCurrent;

    internal void SetCurrent(bool isCurrent) => IsCurrent = isCurrent;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly INavigationStore _navigationStore;
    private readonly LocalizationCatalog _localization;
    private string _currentTitle = string.Empty;
    private string _currentDescription = string.Empty;
    private string _searchText = string.Empty;
    private NavigationModule _currentModule;

    public MainViewModel(INavigationStore navigationStore, LocalizationCatalog localization)
    {
        _navigationStore = navigationStore;
        _localization = localization;
        var routesByGroup = FrontendRouteRegistry.All
            .GroupBy(route => route.Group)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var moduleOrder = new[] { "Library", "Deployment", "Tools", "Analysis", "System" };
        Modules = [.. moduleOrder.Select(group => new NavigationModule(
            group,
            _localization.GetString($"Nav.Group.{group}"),
            [.. routesByGroup[group].Select(route => new NavigationItem(
                route.Key,
                route.Group,
                _localization.GetString(route.TitleKey)))]))];
        _currentModule = Modules[0];
        NavigateCommand = new DelegateCommand(Navigate);
        SelectModuleCommand = new DelegateCommand(SelectModule);
        OpenImportCommand = new DelegateCommand(_ => Navigate("Library"));
        OpenDeploymentCommand = new DelegateCommand(_ => Navigate("Deployment.Order"));
        OpenTasksCommand = new DelegateCommand(_ => Navigate("Deployment.Tasks"));
        OpenSettingsCommand = new DelegateCommand(_ => Navigate("System.Settings"));
        _navigationStore.CurrentPageChanged += (_, _) => RefreshCurrentRoute();
        UpdateSelection("Library");
        Navigate("Library");
    }

    public IReadOnlyList<NavigationModule> Modules { get; }

    public NavigationModule CurrentModule
    {
        get => _currentModule;
        private set
        {
            if (SetProperty(ref _currentModule, value))
            {
                OnPropertyChanged(nameof(CurrentModuleTitle));
                OnPropertyChanged(nameof(CurrentSubPages));
            }
        }
    }

    public string CurrentModuleTitle => CurrentModule.Title;

    public IReadOnlyList<NavigationItem> CurrentSubPages => CurrentModule.Pages;

    public object CurrentPage => _navigationStore.CurrentPage;

    public string CurrentRouteKey => _navigationStore.CurrentRouteKey;

    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    public string CurrentDescription { get => _currentDescription; private set => SetProperty(ref _currentDescription, value); }

    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    public string AppTitle => _localization.GetString("Frontend.AppTitle");

    public string SearchWatermark => _localization.GetString("Frontend.Search");

    public string ImportLabel => _localization.GetString("Frontend.Import");

    public string DeployLabel => _localization.GetString("Frontend.Deploy");

    public string TasksLabel => _localization.GetString("Frontend.Tasks");

    public ICommand NavigateCommand { get; }

    public ICommand SelectModuleCommand { get; }

    public ICommand OpenImportCommand { get; }

    public ICommand OpenDeploymentCommand { get; }

    public ICommand OpenTasksCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public event EventHandler<string[]>? ImportArchivesRequested;

    public void ReceiveImportedArchives(string[] archivePaths)
    {
        ArgumentNullException.ThrowIfNull(archivePaths);
        if (archivePaths.Length > 0)
        {
            ImportArchivesRequested?.Invoke(this, archivePaths);
        }
    }

    private void Navigate(object? parameter)
    {
        if (parameter is not string routeKey)
        {
            return;
        }

        if (string.Equals(_navigationStore.CurrentRouteKey, routeKey, StringComparison.Ordinal))
        {
            return;
        }

        UpdateSelection(routeKey);
        _navigationStore.Navigate(routeKey);
        RefreshCurrentRoute();
        OnPropertyChanged(nameof(CurrentPage));
    }

    private void SelectModule(object? parameter)
    {
        if (parameter is not string moduleKey || string.Equals(CurrentModule.Key, moduleKey, StringComparison.Ordinal))
        {
            return;
        }

        var module = Modules.First(item => string.Equals(item.Key, moduleKey, StringComparison.Ordinal));
        CurrentModule = module;
        Navigate(module.Pages[0].RouteKey);
    }

    private void RefreshCurrentRoute()
    {
        var route = FrontendRouteRegistry.Get(_navigationStore.CurrentRouteKey);
        CurrentTitle = _localization.GetString(route.TitleKey);
        CurrentDescription = _localization.GetString(route.DescriptionKey);
        if (_navigationStore.CurrentPage is FrontendPageViewModel page)
        {
            page.Description = CurrentDescription;
        }

        if (_navigationStore.CurrentPage is Pages.LibraryPageViewModel library)
        {
            _ = library.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.DeploymentOrderPageViewModel deployment)
        {
            _ = deployment.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.SettingsPageViewModel settings)
        {
            _ = settings.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.TagManagementPageViewModel tags)
        {
            _ = tags.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.AutoTagPairingPageViewModel pairing)
        {
            _ = pairing.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.NexusDownloadPageViewModel)
        {
            OnPropertyChanged(nameof(CurrentPage));
        }

        if (_navigationStore.CurrentPage is Pages.PatchResourceViewerPageViewModel resourceViewer)
        {
            _ = resourceViewer.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.ModelPreviewPageViewModel modelPreview)
        {
            _ = modelPreview.InitializeAsync();
        }

        if (_navigationStore.CurrentPage is Pages.DiagnosticsPageViewModel diagnostics)
        {
            _ = diagnostics.InitializeAsync();
        }
    }

    private void UpdateSelection(string routeKey)
    {
        var route = FrontendRouteRegistry.Get(routeKey);
        if (!string.Equals(CurrentModule.Key, route.Group, StringComparison.Ordinal))
        {
            CurrentModule = Modules.First(item => string.Equals(item.Key, route.Group, StringComparison.Ordinal));
        }

        foreach (var module in Modules)
        {
            module.SetCurrent(string.Equals(module.Key, route.Group, StringComparison.Ordinal));
            foreach (var item in module.Pages)
            {
                item.SetCurrent(string.Equals(item.RouteKey, routeKey, StringComparison.Ordinal));
            }
        }
    }
}
