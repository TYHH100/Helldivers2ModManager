using CommunityToolkit.Mvvm.ComponentModel;
using Helldivers2ModManager.Models;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helldivers2ModManager.ViewModels;

/// <summary>
/// A preview-local copy of one manifest option. Changing it never mutates the active
/// profile; it only asks the model preview to expand a different set of Include paths.
/// </summary>
internal sealed partial class ModelPreviewOptionViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public int Index { get; }
    public string Name { get; }
    public string Description { get; }
    public ImageSource? Image { get; }
    public IReadOnlyList<ModelPreviewSubOptionViewModel> SubOptions { get; }
    public bool HasSubOptions => SubOptions.Count > 0;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private ModelPreviewSubOptionViewModel? _selectedSubOption;

    public int SelectedSubOptionIndex => SelectedSubOption?.Index ?? 0;

    public ModelPreviewOptionViewModel(
        int index,
        ModOption option,
        DirectoryInfo modDirectory,
        bool enabled,
        int selectedSubOption,
        Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(modDirectory);
        ArgumentNullException.ThrowIfNull(selectionChanged);

        Index = index;
        Name = option.Name;
        Description = option.Description;
        Image = LoadImage(modDirectory, option.Image);
        _selectionChanged = selectionChanged;
        SubOptions = option.SubOptions?
            .Select((subOption, subIndex) => new ModelPreviewSubOptionViewModel(
                subIndex,
                subOption.Name,
                subOption.Description,
                LoadImage(modDirectory, subOption.Image)))
            .ToArray() ?? [];
        _enabled = enabled;
        _selectedSubOption = SubOptions.Count == 0
            ? null
            : SubOptions[Math.Clamp(selectedSubOption, 0, SubOptions.Count - 1)];
    }

    partial void OnEnabledChanged(bool value) => _selectionChanged();

    partial void OnSelectedSubOptionChanged(ModelPreviewSubOptionViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedSubOptionIndex));
        _selectionChanged();
    }

    private static ImageSource? LoadImage(DirectoryInfo modDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(modDirectory.FullName, relativePath));
            if (!File.Exists(fullPath))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A bad optional manifest thumbnail must not prevent model inspection.
            return null;
        }
    }
}

internal sealed record ModelPreviewSubOptionViewModel(
    int Index,
    string Name,
    string Description,
    ImageSource? Image)
{
    public override string ToString() => Name;
}
