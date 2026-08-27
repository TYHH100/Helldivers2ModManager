using System.Collections.Concurrent;
using Helldivers2ModManager.Models;
using Helldivers2ModManager.Services;
using Helldivers2ModManager.Services.Nexus;
using Helldivers2ModManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Helldivers2ModManager.Adapters;

/// <summary>
/// Owns the one-to-one mapping between persisted mod identities and Dashboard view
/// wrappers after the responsibility has been removed from the legacy backend service.
/// </summary>
internal interface IModViewModelFactory
{
    ModViewModel GetOrCreate(ModData mod);

    void Clear();
}

internal sealed class ModViewModelFactory : IModViewModelFactory
{
    private readonly ConcurrentDictionary<Guid, ModViewModel> _viewModels = new();
    private readonly Func<ModData, ModViewModel> _createViewModel;
    private readonly object _clearLock = new();

    [ActivatorUtilitiesConstructor]
    public ModViewModelFactory(
        ILogger<ModService> logger,
        SettingsService settingsService,
        INexusModsService nexusModsService,
        LocalizationService localizationService,
        VersionCheckService versionCheckService)
        : this(mod => new ModViewModel(
            mod,
            logger,
            settingsService,
            nexusModsService,
            localizationService,
            versionCheckService))
    {
    }

    internal ModViewModelFactory(Func<ModData, ModViewModel> createViewModel)
    {
        ArgumentNullException.ThrowIfNull(createViewModel);
        _createViewModel = createViewModel;
    }

    public ModViewModel GetOrCreate(ModData mod)
    {
        ArgumentNullException.ThrowIfNull(mod);
        return _viewModels.GetOrAdd(mod.Manifest.Guid, static (_, state) => state.create(state.mod), (create: _createViewModel, mod));
    }

    public void Clear()
    {
        lock (_clearLock)
        {
            foreach (var pair in _viewModels)
                pair.Value.Dispose();

            _viewModels.Clear();
        }
    }
}

