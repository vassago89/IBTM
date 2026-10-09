using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Hik;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IBTM;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(
        this IServiceCollection services,
        MachineSettings settings)
    {
        services.TryAddSingleton<ApplicationLog>();
        services.TryAddSingleton<ILoggerFactory>(provider => provider.GetRequiredService<ApplicationLog>().CreateLoggerFactory());
        services.AddLogging();
        services.TryAddSingleton<MachineStore>();
        services.TryAddSingleton<RecipeManager>();
        var hardware = settings.HardwareSections;

        services
            .AddSingleton(settings)
            .AddSingleton<IEnumerable<HardwareSettings>>(hardware)
            .AddSingleton<IoSignals>()
            .AddSingleton<IReadOnlyDictionary<InputIo, int>>(
                hardware.OfType<InputHardwareSettings>()
                    .SelectMany(section => section.Inputs)
                    .ToDictionary())
            .AddSingleton<IReadOnlyDictionary<OutputIo, OutputHardware>>(
                hardware.OfType<IoHardwareSettings>()
                    .SelectMany(section => section.Outputs)
                    .ToDictionary(
                        mapping => mapping.Key,
                        mapping =>
                            new OutputHardware
                            {
                                Number = mapping.Value.Number,
                                OffNumber = mapping.Value.OffNumber,
                                Feedback = mapping.Value.Feedback,
                            }));

        services
            .AddSingleton(settings.Units)
            .AddSingleton(settings.Options)
            .AddSingleton(settings.RecipeSelection)
            .AddSingleton(settings.PcbHistory)
            .AddSingleton(settings.CarrierReference)
            .AddSingleton(settings.PcbSupply)
            .AddSingleton(settings.PcbPlacementHandler)
            .AddSingleton(settings.BoltFeeder)
            .AddSingleton(settings.BoltFastening)
            .AddSingleton(settings.InspectionGantry)
            .AddSingleton(settings.InspectionCamera)
            .AddSingleton(settings.Lighting)
            .AddSingleton(settings.NgCarrierTransfer)
            .AddSingleton(settings.NgConveyor)
            .AddSingleton(settings.Conveyor)
            .AddSingleton(settings.Hantas)
            .AddSingleton<OperationCancellation>();

        services
            .AddKeyedSingleton<IBoltHead, AdcBoltHead>(FasteningHead.Shooting)
            .AddKeyedSingleton<IBoltHead, AdcBoltHead>(FasteningHead.Pickup);

        services
            .AddKeyedSingleton<ConveyorStation>(MotionGroup.PcbPlacementHandler,
                (provider, _) => ConveyorStation.CreatePcbPlacement(provider.GetRequiredService<IIoService>()))
            .AddKeyedSingleton<ConveyorStation>(MotionGroup.BoltFastening,
                (provider, _) => ConveyorStation.CreateBoltFastening(provider.GetRequiredService<IIoService>()))
            .AddKeyedSingleton<ConveyorStation>(MotionGroup.InspectionGantry,
                (provider, _) => ConveyorStation.CreateInspection(provider.GetRequiredService<IIoService>()))
            .AddSingleton<PcbSupplier>()
            .AddSingleton<IPcbSupplyHandoff>(provider => provider.GetRequiredService<PcbSupplier>())
            .AddSingleton<PcbPlacer>()
            .AddSingleton<BoltFasteningStation>()
            .AddSingleton<BoltFeederUnit>()
            .AddSingleton<InspectionStation>()
            .AddSingleton<NgCarrierConveyor>()
            .AddSingleton<MainConveyor>();

        foreach (var group in Enum.GetValues<MotionGroup>())
        {
            services.AddKeyedSingleton<MotionStatus>(group,
                (provider, _) => new MotionStatus(provider.GetRequiredKeyedService<IXyMotion>(group)));
        }

        services
            .AddSingleton<MachineFeedbackMonitor>()
            .AddSingleton<MachineState>()
            .AddSingleton<PcbHistoryWriter>()
            .AddSingleton<InspectionImageLoader>()
            .AddSingleton<PcbResultsViewModel>()
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<IXyMotion>(group)))
            .AddSingleton<IReadOnlyDictionary<MotionGroup, MotionStatus>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<MotionStatus>(group)))
            .AddSingleton<MachineController>();

        services
            .AddSingleton<RecipeEditorViewModel>()
            .AddSingleton<MachineDiagramMapper>()
            .AddSingleton<OperationViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<ManualHardwareViewModel>()
            .AddSingleton<MotionDiagnosticsViewModel>()
            .AddSingleton<TeachingViewModel>()
            .AddSingleton<ResultsViewModel>()
            .AddSingleton<InspectionTeachingViewModel>()
            .AddSingleton<DiagnosticWindowManager>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>();

        return services;
    }

    public static IServiceCollection AddIbtmHardware(
        this IServiceCollection services,
        MachineSettings settings)
    {
        services
            .AddSingleton(settings.Ajin)
            .AddSingleton(settings.AlphaMotion)
            .AddSingleton<AjinController>()
            .AddSingleton<AlphaMotionController>()
            .AddSingleton<IIoService, PhysicalIoService>()
            .AddKeyedSingleton<IAdcBus, AdcBus>(FasteningHead.Pickup)
            .AddKeyedSingleton<IAdcBus, AdcBus>(FasteningHead.Shooting)
            .AddSingleton<ICamera, HikCamera>()
            .AddSingleton<ILightController, MovsLightController>();

        foreach (var (motionSettings, motionHardware) in settings.MotionSections)
        {
            services
                .AddKeyedSingleton(motionHardware.Group, motionHardware)
                .AddKeyedSingleton(motionHardware.Group, motionSettings)
                .AddKeyedSingleton<IXyMotion, AjinMotionService>(motionHardware.Group);
        }

        return services;
    }
}
