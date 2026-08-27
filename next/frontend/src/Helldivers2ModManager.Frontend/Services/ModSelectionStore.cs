using Helldivers2ModManager.Frontend.Models;

namespace Helldivers2ModManager.Frontend.Services;

public sealed class ModSelectionStore
{
    public ModItem? Selected { get; set; }
    public ModItem? ResourceViewer { get; set; }
}
