using System.Text.Json;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Jalium.Core;

internal sealed class DashboardLibraryService(
    ModCatalogService catalog,
    EnabledDataRepository repository,
    ILogger<DashboardLibraryService> logger)
{
    public async Task<ModCatalogResult> LoadAsync(SettingsService settings, CancellationToken cancellationToken = default)
    {
        if (!settings.Initialized)
            throw new InvalidOperationException("Settings must be initialized before the mod library is loaded.");

        var storage = settings.StorageDirectory;
        var catalogResult = await catalog.LoadAsync(storage, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var legacyPath = Path.Combine(storage, "enabled.json");
        List<EnabledData> saved;
        if (File.Exists(legacyPath) && !repository.HasData(storage))
        {
            var legacy = await ReadLegacyAsync(legacyPath, cancellationToken).ConfigureAwait(false);
            try
            {
                await repository.SaveAllAsync(storage, legacy).ConfigureAwait(false);
                if (repository.GetCount(storage) != legacy.Count)
                    throw new InvalidOperationException("Legacy profile migration did not preserve the record count.");
                try
                {
                    File.Move(legacyPath, legacyPath + ".bak");
                }
                catch (IOException ex)
                {
                    logger.LogWarning(ex, "Legacy profile was migrated but could not be backed up");
                }
                saved = legacy;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Profile migration failed; using the original JSON for this session");
                saved = legacy;
            }
        }
        else
        {
            saved = await Task.Run(() => repository.LoadAll(storage), cancellationToken).ConfigureAwait(false);
        }

        var byGuid = catalogResult.Mods.ToDictionary(mod => mod.Manifest.Guid);
        var ordered = new List<ModData>(catalogResult.Mods.Count);
        var missing = new HashSet<Guid>();
        foreach (var data in saved)
        {
            if (!byGuid.Remove(data.Guid, out var mod))
            {
                missing.Add(data.Guid);
                continue;
            }
            mod.ApplyData(data);
            ordered.Add(mod);
        }
        if (settings.AutoRemoveMissingMods && missing.Count > 0)
            await repository.DeleteByGuidsAsync(storage, missing).ConfigureAwait(false);
        foreach (var mod in catalogResult.Mods)
            if (byGuid.Remove(mod.Manifest.Guid))
                ordered.Add(mod);
        return new ModCatalogResult(ordered, catalogResult.Problems);
    }

    public Task SaveAsync(SettingsService settings, IReadOnlyList<ModData> orderedMods)
    {
        if (!settings.Initialized)
            throw new InvalidOperationException("Settings must be initialized before the mod library is saved.");
        var snapshot = orderedMods.Select(mod => new EnabledData
        {
            Guid = mod.Manifest.Guid,
            Enabled = mod.Enabled,
            Toggled = [.. mod.EnabledOptions],
            Selected = [.. mod.SelectedOptions],
            TagIds = [.. mod.TagIds],
        }).ToArray();
        return repository.SaveAllAsync(settings.StorageDirectory, snapshot);
    }

    public Task SaveTagsAsync(SettingsService settings, IReadOnlyList<ModData> mods)
        => repository.SaveTagsAsync(settings.StorageDirectory, mods.Select(mod => new EnabledData
        {
            Guid = mod.Manifest.Guid,
            Enabled = mod.Enabled,
            Toggled = [.. mod.EnabledOptions],
            Selected = [.. mod.SelectedOptions],
            TagIds = [.. mod.TagIds],
        }).ToArray());

    private static async Task<List<EnabledData>> ReadLegacyAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Legacy enabled.json must contain an array.");
        return document.RootElement.EnumerateArray().Select(element => EnabledData.Deserialize(element)).ToList();
    }
}
