using Microsoft.Extensions.DependencyInjection;

namespace Helldivers2ModManager.Core.Deployment;

public static class DeploymentModule
{
    public static IServiceCollection AddDeployment(this IServiceCollection services)
    {
        services.AddSingleton<DeploymentService>();
        return services;
    }
}
