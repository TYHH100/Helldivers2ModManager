using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.Models;

public sealed class EditableTag : ObservableObject
{
    private string _name = string.Empty;
    private string _color = "#FF60CDFF";

    public required Guid Id { get; init; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Color { get => _color; set => SetProperty(ref _color, value); }
}
