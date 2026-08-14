namespace Helldivers2ModManager.Models;

internal sealed class ModGroupSelectionItem
{
    public ModGroup Group { get; }
    public bool IsSelected { get; set; }

    public ModGroupSelectionItem(ModGroup group, bool isSelected = false)
    {
        Group = group;
        IsSelected = isSelected;
    }

    public string Name => Group.Name;
}
