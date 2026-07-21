using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.BoltFastening;

public static class BoltFasteningModule
{
    public const string ServiceKey = "bolt-fastening";

    public static IServiceCollection AddBoltFasteningStation(this IServiceCollection services)
    {
        services.AddSingleton<IBoltFasteningStation, BoltFasteningStation>();
        return services;
    }
}
