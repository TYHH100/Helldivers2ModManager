using Helldivers2ModManager.Core.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helldivers2ModManager.Core.Profiles;

public static class ProfilesModule
{
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        services.AddSingleton<GroupRepository>();
        services.AddSingleton<ModGroupService>();
        services.AddSingleton(provider => new ProfileSaveCoordinator(
            snapshot => provider.GetRequiredService<ProfileRepository>().SaveAsync(snapshot),
            provider.GetRequiredService<ILogger<ProfileSaveCoordinator>>()));
        return services;
    }
}
