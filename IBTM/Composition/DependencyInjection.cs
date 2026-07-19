using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Composition;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(this IServiceCollection services)
    {
        services.AddSingleton<MachineConfig>();
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(ZoneServiceKeys.Zone1);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(ZoneServiceKeys.Zone2);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(ZoneServiceKeys.Zone3);
        services.AddSingleton<IIOService, VirtualIoService>();

        services.AddSingleton<IFiducialService, FiducialService>();
        services.AddSingleton<IBoltService, StubBoltService>();
        services.AddSingleton<IInspectionService, SimulatedInspectionService>();
        services.AddSingleton<ProcessEventHub>();
        services.AddSingleton<ProcessStageRunner>();
        services.AddSingleton<MachineOperations>();
        services.AddSingleton<BoltTighteningService>();
        services.AddSingleton<Zone1Workflow>();
        services.AddSingleton<Zone2Workflow>();
        services.AddSingleton<Zone3Workflow>();
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton<ProcessOrchestrator>();
        services.AddSingleton<RecipeService>();
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>(ZoneServiceKeys.Zone2);
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>(ZoneServiceKeys.Zone3);

        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
