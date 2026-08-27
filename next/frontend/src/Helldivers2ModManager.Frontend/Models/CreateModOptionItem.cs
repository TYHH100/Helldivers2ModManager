using System.Collections.ObjectModel;
using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.Models;

public sealed class CreateModSubOptionItem : ObservableObject
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _includePaths = string.Empty;
    private string? _imagePath;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string IncludePaths { get => _includePaths; set => SetProperty(ref _includePaths, value); }
    public string? ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
}

public sealed class CreateModOptionItem : ObservableObject
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private string _includePaths = string.Empty;
    private string? _imagePath;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string IncludePaths { get => _includePaths; set => SetProperty(ref _includePaths, value); }
    public string? ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }
    public ObservableCollection<CreateModSubOptionItem> SubOptions { get; } = [];
}
