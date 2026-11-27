using DockerBackup.Core.Engine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DockerBackup.Docker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDockerEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DockerEngineOptions>(configuration.GetSection(DockerEngineOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<DockerEngineMonitor>();
        services.AddSingleton<IEngineStatusProvider>(sp => sp.GetRequiredService<DockerEngineMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<DockerEngineMonitor>());
        return services;
    }
}
