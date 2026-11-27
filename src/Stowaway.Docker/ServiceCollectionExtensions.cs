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
        services.AddDatabaseProviders();
        services.AddDestinationProviders();
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

    /// <summary>The destination providers (see <see cref="Destinations.IDestinationProvider"/>), one per destination type.</summary>
    private static IServiceCollection AddDestinationProviders(this IServiceCollection services)
    {
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.LocalFolderProvider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.SmbProvider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.S3Provider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.B2Provider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.AzureProvider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.SftpProvider>();
        services.AddSingleton<Destinations.IDestinationProvider, Destinations.DropboxProvider>();
        services.AddSingleton<Destinations.DestinationProviders>();
        return services;
    }

    /// <summary>
    /// The database providers (see <see cref="Databases.IDatabaseProvider"/>). A database dumped through <c>sh</c> on
    /// Linux only needs its <see cref="Stowaway.Core.Databases.ShellDatabase"/> definition registered here.
    /// </summary>
    private static IServiceCollection AddDatabaseProviders(this IServiceCollection services)
    {
        foreach (var definition in Stowaway.Core.Databases.ShellDatabases.All)
        {
            services.AddSingleton(definition);
        }

        services.AddSingleton<Databases.IDatabaseProvider, Databases.ShellDatabaseProvider>();
        services.AddSingleton<Databases.IDatabaseProvider, Databases.SqlServerProvider>();
        services.AddSingleton<Databases.DatabaseProviders>();
        services.AddSingleton(sp => sp.GetRequiredService<Databases.DatabaseProviders>().Support);
        return services;
    }
}
