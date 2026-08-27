using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Core.Mods;

public static class ModsModule
{
    public static IServiceCollection AddMods(this IServiceCollection services)
    {
        services.AddSingleton<FileHashService>();
        services.AddSingleton<ModDirectoryService>();
        services.AddSingleton<ModTypeDetectionService>();
        services.AddSingleton<AutoTaggingService>();
        services.AddSingleton<ModArchiveService>();
        services.AddSingleton<IRecycleBinAdapter, Win32RecycleBinAdapter>();
        return services;
    }
}
