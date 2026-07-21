using IBTM.Configuration;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Orchestration;
using IBTM.Presentation.Mappers;
using IBTM.Presentation.Shell;
using IBTM.Presentation.ViewModels;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Composition;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(this IServiceCollection services)
    {
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().PcbPlacementMotion);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().Conveyor);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().BoltFastening);
        services.AddSingleton(provider => provider.GetRequiredService<MachineConfig>().Inspection);

        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(1);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(2);
        services.AddKeyedSingleton<IMotionService, VirtualMotionService>(3);
        services.AddSingleton<VirtualIoService>();
        services.AddSingleton<IIoService>(provider => provider.GetRequiredService<VirtualIoService>());
        services.AddSingleton<IConveyorServo, VirtualConveyorServo>();
        services.AddSingleton<Conveyor>();

        services.AddSingleton<IBoltService, VirtualBoltService>();
        services.AddKeyedSingleton<ICameraStreamService>(
            2,
            (_, _) => new VirtualCameraStreamService(inspection: false));
        services.AddKeyedSingleton<ICameraStreamService>(
            3,
            (_, _) => new VirtualCameraStreamService(inspection: true));
        services.AddSingleton<ProcessEvents>();
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<PcbPlacementStation>(
            provider,
            provider.GetRequiredKeyedService<IMotionService>(1)));
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<BoltFasteningStation>(
            provider,
            provider.GetRequiredKeyedService<IMotionService>(2),
            provider.GetRequiredKeyedService<ICameraStreamService>(2)));
        services.AddSingleton(provider => ActivatorUtilities.CreateInstance<InspectionStation>(
            provider,
            provider.GetRequiredKeyedService<IMotionService>(3),
            provider.GetRequiredKeyedService<ICameraStreamService>(3)));
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton<ProcessOrchestrator>();
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
