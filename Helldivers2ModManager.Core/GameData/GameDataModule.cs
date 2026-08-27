using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Core.GameData;

public static class GameDataModule
{
    public static IServiceCollection AddGameData(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<GameArchiveService>();
        return services;
    }
}