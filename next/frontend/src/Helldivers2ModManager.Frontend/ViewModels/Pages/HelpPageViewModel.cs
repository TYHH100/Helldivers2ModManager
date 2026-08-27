using System.Collections.ObjectModel;
using System.Windows.Input;
using Helldivers2ModManager.Core.Localization;
using Helldivers2ModManager.Frontend.Common;
using Helldivers2ModManager.Frontend.Navigation;

namespace Helldivers2ModManager.Frontend.ViewModels.Pages;

public sealed record HelpRouteItem(string RouteKey, string Module, string Title, string Description, bool IsDiagnostic);

public sealed class HelpPageViewModel : FrontendPageViewModel
{
    private readonly INavigationStore _navigation;
    private readonly LocalizationCatalog _localization;

    public ObservableCollection<HelpRouteItem> Routes { get; } = [];
    public ICommand NavigateCommand { get; }

    public override string Title => _localization.GetString("Nav.Help");

    public HelpPageViewModel(INavigationStore navigation, LocalizationCatalog localization)
    {
        _navigation = navigation;
        _localization = localization;
        NavigateCommand = new DelegateCommand(parameter =>
        {
            if (parameter is string routeKey)
            {
                _navigation.Navigate(routeKey);
            }
        });

        var moduleOrder = new[] { "Library", "Deployment", "Tools", "Analysis", "System" };
        foreach (var route in FrontendRouteRegistry.All
                     .OrderBy(item => Array.IndexOf(moduleOrder, item.Group))
                     .ThenBy(item => item.Key))
        {
            Routes.Add(new(
                route.Key,
                _localization.GetString($"Nav.Group.{route.Group}"),
                _localization.GetString(route.TitleKey),
                _localization.GetString(route.DescriptionKey),
                route.IsDiagnostic));
        }
    }
}
