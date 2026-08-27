using Helldivers2ModManager.Frontend.Common;

namespace Helldivers2ModManager.Frontend.ViewModels;

public abstract class FrontendPageViewModel : ObservableObject
{
    public abstract string Title { get; }

    public string Description { get; set; } = string.Empty;
}
