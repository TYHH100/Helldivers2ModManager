using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.Models;

public sealed class EditableModOption : ObservableObject
{
    private bool _isEnabled;
    private int _selectedSubOption;

    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> SubOptions { get; init; } = [];
    public bool HasSubOptions => SubOptions.Count > 0;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public int SelectedSubOption
    {
        get => _selectedSubOption;
        set => SetProperty(ref _selectedSubOption, value);
    }
}
