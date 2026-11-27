using Stowaway.Core.Engine;
using Stowaway.Core.Inventory;
using Stowaway.Core.Plans;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Plans;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stowaway.Docker;

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

    /// <summary>Backup engine. The host must also register <see cref="IBackupEvents"/>, the stores and a secret protector.</summary>
    public static IServiceCollection AddBackupEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BackupOptions>(configuration.GetSection(BackupOptions.SectionName));
        services.AddSingleton<ResticRunner>();
        services.AddSingleton<ResticRepository>();
        services.AddSingleton<DumpService>();
        services.AddSingleton<SqlServerDumpService>();
        services.AddSingleton<Restore.SqlServerDrill>();
        services.AddSingleton<DatabaseProbe>();
        services.AddSingleton<DropboxAuthService>();
        services.AddSingleton<DestinationService>();
        services.AddSingleton<Restore.SnapshotCatalog>();
        services.AddSingleton<Restore.RestoreService>();
        services.AddSingleton<Restore.ContainerRecreator>();
        services.AddSingleton<Restore.DrillService>();
        services.AddSingleton<BackupService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
        services.AddHostedService<DestinationMonitor>();

        services.AddSingleton<ProtectionTracker>();
        services.AddSingleton<IProtectionSource>(sp => sp.GetRequiredService<ProtectionTracker>());
        services.AddSingleton<IRunObserver>(sp => sp.GetRequiredService<ProtectionTracker>());
        services.AddHostedService(sp => sp.GetRequiredService<ProtectionTracker>());
        services.AddSingleton<PlanService>();
        services.AddHostedService<PlanScheduler>();
        services.AddHostedService<MaintenanceScheduler>();
        services.AddHostedService<DrillScheduler>();
        return services;
    }
}
