using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.Inspection;

public static class InspectionModule
{
    public const string ServiceKey = "inspection";

    public static IServiceCollection AddInspectionStation(this IServiceCollection services)
    {
        services.AddSingleton<IInspectionStation, InspectionStation>();
        return services;
    }
}
