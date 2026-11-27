using DockerBackup.Core.Engine;
using DockerBackup.Core.Inventory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DockerBackup.Docker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDockerEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DockerEngineOptions>(configuration.GetSection(DockerEngineOptions.SectionName));
        services.Configure<InventoryOptions>(configuration.GetSection(InventoryOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<DockerEngineMonitor>();
        services.AddSingleton<IEngineStatusProvider>(sp => sp.GetRequiredService<DockerEngineMonitor>());
        services.AddHostedService(sp => sp.GetRequiredService<DockerEngineMonitor>());

        services.AddSingleton<InventoryService>();
        services.AddSingleton<IInventoryProvider>(sp => sp.GetRequiredService<InventoryService>());
        services.AddHostedService(sp => sp.GetRequiredService<InventoryService>());
        return services;
    }
}
