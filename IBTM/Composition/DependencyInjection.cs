using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Composition;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(this IServiceCollection services)
    {
        services.AddSingleton<MachineConfig>();
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().Runtime);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().PcbPlacement);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().BoltFastening);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().Inspection);

        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(PcbPlacementModule.ServiceKey);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(BoltFasteningModule.ServiceKey);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(InspectionModule.ServiceKey);
        services.AddSingleton<IIOService, VirtualIoService>();

        services.AddSingleton<IFiducialService, SimulatedFiducialService>();
        services.AddSingleton<IBoltService, StubBoltService>();
        services.AddSingleton<IInspectionService, SimulatedInspectionService>();
        services.AddSingleton<ProcessEventHub>();
        services.AddSingleton<ProcessStageRunner>();
        services.AddPcbPlacementStation();
        services.AddBoltFasteningStation();
        services.AddInspectionStation();
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton<ProcessOrchestrator>();
        services.AddSingleton<RecipeService>();
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>(BoltFasteningModule.ServiceKey);
        services.AddKeyedSingleton<ICameraStreamService, VirtualCameraStreamService>(InspectionModule.ServiceKey);

        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
