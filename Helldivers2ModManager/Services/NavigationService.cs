using Helldivers2ModManager.Core.UI;
using Helldivers2ModManager.Stores;

namespace Helldivers2ModManager.Services;

internal sealed class NavigationService : INavigationService
{
    private readonly Func<NavigationStore> _storeFactory;

    public NavigationService(Func<NavigationStore> storeFactory)
    {
        _storeFactory = storeFactory;
    }

    public void Navigate(Type destinationType, bool root = false) =>
        _storeFactory().Navigate(destinationType, root);

    public bool GoBack() => _storeFactory().GoBack();
}
