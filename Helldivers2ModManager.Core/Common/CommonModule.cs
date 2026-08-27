using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Core.Common;

public static class CommonModule
{
    public static IServiceCollection AddCommon(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IBackgroundTaskRunner, BackgroundTaskRunner>();
        return services;
    }
}
