namespace Helldivers2ModManager.Models;

/// <summary>
/// Keeps the model explicitly selected from the dashboard available while the
/// preview page is refreshing its eventually-consistent ModService snapshot.
/// </summary>
internal static class ModelPreviewModSelection
{
    public static ModelPreviewModListState Resolve(
        IEnumerable<ModData> serviceMods,
        ModData? preferredMod)
    {
        ArgumentNullException.ThrowIfNull(serviceMods);

        var mods = new List<ModData>();
        var addedModIds = new HashSet<Guid>();
        var preferredWasAdded = false;

        foreach (var serviceMod in serviceMods)
        {
            if (preferredMod is not null && serviceMod.Manifest.Guid == preferredMod.Manifest.Guid)
            {
                if (!preferredWasAdded)
                {
                    mods.Add(preferredMod);
                    addedModIds.Add(preferredMod.Manifest.Guid);
                    preferredWasAdded = true;
                }

                continue;
            }

            if (addedModIds.Add(serviceMod.Manifest.Guid))
                mods.Add(serviceMod);
        }

        if (preferredMod is not null && !preferredWasAdded)
            mods.Add(preferredMod);

        return new ModelPreviewModListState(mods, preferredMod ?? mods.FirstOrDefault());
    }
}

internal sealed record ModelPreviewModListState(
    IReadOnlyList<ModData> Mods,
    ModData? SelectedMod);
