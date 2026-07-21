using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.PcbPlacement;

public static class PcbPlacementModule
{
    public const string ServiceKey = "pcb-placement";

    public static IServiceCollection AddPcbPlacementStation(this IServiceCollection services)
    {
        services.AddSingleton<IPcbPlacementStation, PcbPlacementStation>();
        return services;
    }
}
