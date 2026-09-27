using Helldivers2ModManager.Services;

namespace Helldivers2ModManager.ViewModels;

internal static class ModGroupViewModelFilter
{
    public static IEnumerable<ModViewModel> FilterModViewModels(this ModGroupService service,
        IEnumerable<ModViewModel> mods)
    {
        var byGuid = new Dictionary<Guid, ModViewModel>();
        foreach (var mod in mods)
            byGuid.TryAdd(mod.Guid, mod);
        foreach (var data in service.FilterMods(byGuid.Values.Select(mod => mod.Data)))
            if (byGuid.Remove(data.Manifest.Guid, out var viewModel))
                yield return viewModel;
    }
}
