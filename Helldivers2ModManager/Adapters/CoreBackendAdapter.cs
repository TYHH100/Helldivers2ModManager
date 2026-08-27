using System.IO;
using Helldivers2ModManager.Core.Common;
using Helldivers2ModManager.Core.Deployment;
using Helldivers2ModManager.Core.GameData;
using Helldivers2ModManager.Core.Mods;
using Helldivers2ModManager.Core.Persistence;
using Helldivers2ModManager.Core.Preview;
using Helldivers2ModManager.Core.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Adapters;

/// <summary>
/// Explicit Core registration used while legacy facades are switched module-by-module.
/// The old database is intentionally untouched; Core owns a separate redesigned store.
/// </summary>
internal static class CoreBackendAdapter
{
    public static IServiceCollection AddCoreBackend(this IServiceCollection services)
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, "mod_manager.core.db");

        return services
            .AddCommon()
            .AddPersistence(databasePath)
            .AddGameData()
            .AddMods()
            .AddDeployment()
            .AddProfiles()
            .AddSingleton<Core.Analysis.ArmorReuseService>()
            .AddSingleton<Core.Versioning.PatchStructureAnalyzer>()
            .AddSingleton<Core.Versioning.VersionCheckService>(provider => new Core.Versioning.VersionCheckService(
                provider.GetRequiredService<Core.Versioning.PatchStructureAnalyzer>(),
                () =>
                {
                    var settings = provider.GetRequiredService<Services.SettingsService>();
                    return settings.Initialized && Directory.Exists(settings.GameDirectory)
                        ? new DirectoryInfo(settings.GameDirectory)
                        : null;
                },
                provider.GetRequiredService<GameArchiveService>()))
            .AddSingleton<Core.Analysis.ModConflictService>()
            .AddSingleton<Core.Repair.MetadataRepairService>()
            .AddSingleton<Core.Repair.CompanionRecoveryService>()
            .AddSingleton<Core.Repair.BackupService>()
            .AddSingleton<Core.Repair.AssistedRepairService>()
            .AddSingleton<Core.Repair.BatchRepairService>(provider => new Core.Repair.BatchRepairService(
                provider.GetRequiredService<Core.Repair.MetadataRepairService>(),
                provider.GetRequiredService<Core.Repair.AssistedRepairService>(),
                provider.GetRequiredService<Core.Versioning.PatchStructureAnalyzer>(),
                provider.GetRequiredService<Core.Repair.CompanionRecoveryService>(),
                () =>
                {
                    var settings = provider.GetRequiredService<Services.SettingsService>();
                    return settings.Initialized && Directory.Exists(settings.GameDirectory)
                        ? new DirectoryInfo(settings.GameDirectory)
                        : null;
                }))
            .AddSingleton<PatchResourceInspector>()
            .AddSingleton<IModViewModelFactory, ModViewModelFactory>();
    }

    /// <summary>
    /// ViewModels remain the owner of display instances. During the staged backend
    /// switchover this identity map prevents a facade from creating a second wrapper
    /// for the same Core ModRecord when ownership moves off ModService.
    /// </summary>
    internal sealed class ViewModelIdentityMap<TKey, TValue>
        where TKey : notnull
        where TValue : class
    {
        private readonly Dictionary<TKey, TValue> _values = [];
        private readonly object _lock = new();

        public TValue GetOrCreate(TKey key, Func<TKey, TValue> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            lock (_lock)
            {
                if (_values.TryGetValue(key, out var value))
                    return value;

                value = factory(key);
                _values[key] = value;
                return value;
            }
        }

        public bool Remove(TKey key)
        {
            lock (_lock)
                return _values.Remove(key);
        }

        public void Clear() => _values.Clear();
    }
}



