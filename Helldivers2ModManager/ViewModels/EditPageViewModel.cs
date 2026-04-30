using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.ViewModels;

[RegisterService(ServiceLifetime.Transient)]
internal sealed partial class EditPageViewModel : PageViewModelBase
{
    public override string Title => "Mods Optional";

    public ModViewModel? EditMod => _editModStore.CurrentMod;

    [ObservableProperty]
    private ImageSource? _previewImageSource;

    [ObservableProperty]
    private Visibility _imagePreviewVisibility = Visibility.Collapsed;

    private readonly NavigationStore _navStore;
    private readonly EditModStore _editModStore;

    public EditPageViewModel(NavigationStore navStore, EditModStore editModStore)
    {
        _navStore = navStore;
        _editModStore = editModStore;
    }

    [RelayCommand]
    void Done()
    {
        _editModStore.CurrentMod = null;
        _navStore.Navigate<DashboardPageViewModel>();
    }

    [RelayCommand]
    void Cancel()
    {
        _editModStore.CurrentMod = null;
        _navStore.Navigate<DashboardPageViewModel>();
    }

    [RelayCommand]
    void ShowImagePreview(ImageSource imageSource)
    {
        PreviewImageSource = imageSource;
        ImagePreviewVisibility = Visibility.Visible;
    }

    [RelayCommand]
    void HideImagePreview()
    {
        ImagePreviewVisibility = Visibility.Hidden;
        PreviewImageSource = null;
    }
}