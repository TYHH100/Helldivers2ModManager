using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Core.Persistence;

public static class PersistenceModule
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));

        return services
            .AddSingleton(_ => new Database(databasePath))
            .AddSingleton<PreferenceRepository>()
            .AddSingleton<ProfileRepository>()
            .AddSingleton<EnabledStateRepository>()
            .AddSingleton<FileHashRepository>()
            .AddSingleton<IFileHashRepository>(provider => provider.GetRequiredService<FileHashRepository>())
            .AddSingleton<VersionResultRepository>()
            .AddSingleton<JsonCacheRepository>();
    }
}
