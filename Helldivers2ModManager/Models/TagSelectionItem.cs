namespace Helldivers2ModManager.Models;

public sealed class TagSelectionItem
{
    public ModTag Tag { get; }
    public bool IsSelected { get; set; }

    public TagSelectionItem(ModTag tag, bool isSelected = false)
    {
        Tag = tag;
        IsSelected = isSelected;
    }

    public Guid Id => Tag.Id;
    public string Name => Tag.Name;
    public string Color => Tag.Color;
}