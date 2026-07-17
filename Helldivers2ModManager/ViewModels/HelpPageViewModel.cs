using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class HelpPageViewModel : PageViewModelBase
{
    private static readonly ProcessStartInfo s_documentation = new(
        "https://teutinsa.github.io/hd2mm-site/index.html")
    {
        UseShellExecute = true
    };
    private static readonly ProcessStartInfo s_issueTracker = new(
        "https://github.com/TYHH100/Helldivers2ModManager/issues")
    {
        UseShellExecute = true
    };

    public override string Title => _localizationService["MainWindow.Help"];

    private readonly NavigationStore _navigationStore;
    private readonly LocalizationService _localizationService;

    public HelpPageViewModel(NavigationStore navigationStore, LocalizationService localizationService)
    {
        _navigationStore = navigationStore;
        _localizationService = localizationService;

        _localizationService.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
        };
    }

    [RelayCommand]
    void Back()
    {
        _navigationStore.Navigate<DashboardPageViewModel>();
    }

    [RelayCommand]
    private static void OpenDocumentation() => Process.Start(s_documentation);

    [RelayCommand]
    private static void ReportIssue() => Process.Start(s_issueTracker);
}
