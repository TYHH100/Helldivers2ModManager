using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.ViewModels.Create;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// Shared state and behavior for manifest creation and existing-manifest editing.
/// Page-specific persistence remains in the derived use-case view models.
/// </summary>
internal abstract partial class ManifestEditorViewModel : PageViewModelBase
{
    protected LocalizationService Localizer { get; }
    protected IDialogService DialogService { get; }

    protected string? BrowsedIconSourcePath { get; set; }

    [ObservableProperty]
    private string _modName = string.Empty;

    [ObservableProperty]
    private string _modDescription = string.Empty;

    [ObservableProperty]
    private string _iconPath = string.Empty;

    [ObservableProperty]
    private bool _isJsonMode;

    [ObservableProperty]
    private string _jsonContent = string.Empty;

    protected ObservableCollection<CreateModOptionViewModel> EditorOptions { get; } = [];

    protected abstract string IconBaseDirectory { get; }

    protected abstract string BrowseIconTitle { get; }

    protected virtual ImageSource? FallbackIcon => null;

    protected ManifestEditorViewModel(LocalizationService localizer, IDialogService dialogService)
    {
        Localizer = localizer;
        DialogService = dialogService;
    }

    public ImageSource? IconPreview
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(IconPath))
            {
                var candidate = Path.IsPathRooted(IconPath)
                    ? IconPath
                    : string.IsNullOrWhiteSpace(IconBaseDirectory)
                        ? null
                        : Path.Combine(IconBaseDirectory, IconPath);
                if (candidate is not null && File.Exists(candidate))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(candidate);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        return bitmap;
                    }
                    catch (Exception exception) when (exception is IOException or NotSupportedException)
                    {
                        // Invalid user images fall back to the current mod icon or no preview.
                    }
                }
            }
            return FallbackIcon;
        }
    }

    [RelayCommand]
    private void BrowseIcon()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = BrowseIconTitle,
            Filter = Localizer["Common.SelectImageFilter"],
            InitialDirectory = string.IsNullOrWhiteSpace(IconBaseDirectory) ? null : IconBaseDirectory
        };
        if (dialog.ShowDialog() != true)
            return;
        BrowsedIconSourcePath = dialog.FileName;
        IconPath = Path.GetFileName(dialog.FileName);
    }

    [RelayCommand]
    private void AddOption() => EditorOptions.Add(new CreateModOptionViewModel(Localizer, DialogService)
    {
        SourceDirectory = IconBaseDirectory
    });

    [RelayCommand]
    private void RemoveOption(CreateModOptionViewModel option) => EditorOptions.Remove(option);

    partial void OnIconPathChanged(string value) => OnPropertyChanged(nameof(IconPreview));

    partial void OnModNameChanged(string value) => OnEditorIdentityChanged();

    partial void OnIsJsonModeChanged(bool value)
    {
        if (value)
            SwitchToJsonMode();
        else
            SwitchToVisualMode();
        OnEditorModeChanged();
    }

    protected virtual void OnEditorModeChanged()
    {
    }

    protected virtual void OnEditorIdentityChanged()
    {
    }

    protected abstract void SwitchToJsonMode();

    protected abstract void SwitchToVisualMode();
}
