using System.Collections.Generic;
using System.Linq;
using System;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
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
            .AddSingleton<IReadOnlyList<MotionHardwareSettings>>(
                hardware.OfType<MotionHardwareSettings>().ToArray())
            .AddSingleton(
                provider => new IoSignals(hardware, provider.GetRequiredService<IIoService>()))
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

        services.AddSingleton<IReadOnlyDictionary<HardwareArea, TeachingIoGroup[]>>(provider =>
        {
            var io = provider.GetRequiredService<IoSignals>();
            var machine = provider.GetRequiredService<MachineController>();
            return new Dictionary<HardwareArea, TeachingIoGroup[]>
            {
                [HardwareArea.PcbSupply] = [
                    new(settings.PcbSupplyHardware, settings.PcbSupplyHardware.Outputs.Keys,
                        io, machine),
                ],
                [HardwareArea.PcbPlacementHandler] = [
                    new(settings.PcbPlacementStationHardware,
                        [OutputIo.PcbPlacementBackupPlateUp, OutputIo.PcbPlacementStopperUp],
                        io, machine),
                    new(settings.PcbPlacementHandlerHardware, settings.PcbPlacementHandlerHardware.Outputs.Keys,
                        io, machine),
                ],
                [HardwareArea.BoltFastening] = [
                    new(settings.BoltFasteningStationHardware,
                        [OutputIo.BoltFasteningBackupPlateUp, OutputIo.BoltFasteningStopperUp],
                        io, machine),
                    new(settings.BoltFasteningHardware, settings.BoltFasteningHardware.Outputs.Keys,
                        io, machine),
                    new(settings.IoBoltHardware, settings.IoBoltHardware.Outputs.Keys,
                        io, machine),
                    new(settings.BoltFeederHardware, settings.BoltFeederHardware.Outputs.Keys,
                        io, machine),
                ],
                [HardwareArea.InspectionGantry] = [
                    new(settings.InspectionStationHardware,
                        [OutputIo.InspectionBackupPlateUp, OutputIo.InspectionStopperUp],
                        io, machine),
                    new(settings.NgCarrierTransferHardware, settings.NgCarrierTransferHardware.Outputs.Keys,
                        io, machine),
                    new(settings.NgShuttleHardware, settings.NgShuttleHardware.Outputs.Keys,
                        io, machine),
                ],
            };
        });

        services.AddSingleton(provider => new MainConveyor(
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<ConveyorSettings>(),
            provider.GetRequiredService<OperationCancellation>(),
            provider.GetRequiredService<PcbPlacer>().Station,
            provider.GetRequiredService<BoltFasteningStation>().Station,
            provider.GetRequiredService<InspectionStation>(),
            provider.GetRequiredService<UnitSettings>()));

        services
            .AddKeyedSingleton<IBoltHead>(
                FasteningHead.Shooting,
                (provider, _) => new AdcBoltHead(
                    provider.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting),
                    provider.GetRequiredService<IIoService>(), FasteningHead.Shooting,
                    settings.Hantas,
                    settings.Hantas.ShootingSlaveAddress,
                    settings.Hantas.ShootingPortName,
                    settings.Hantas.ShootingBaudRate,
                    provider.GetRequiredService<ILogger<AdcBoltHead>>()))
            .AddKeyedSingleton<IBoltHead>(
                FasteningHead.Pickup,
                (provider, _) => new AdcBoltHead(
                    provider.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup),
                    provider.GetRequiredService<IIoService>(), FasteningHead.Pickup,
                    settings.Hantas,
                    settings.Hantas.PickupSlaveAddress,
                    settings.Hantas.PickupPortName,
                    settings.Hantas.PickupBaudRate,
                    provider.GetRequiredService<ILogger<AdcBoltHead>>()));

        services
            .AddSingleton(
                provider =>
                    new PcbSupplier(
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.PcbSupply],
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.PcbSupply],
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbSupply,
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>()))
            .AddSingleton(
                provider =>
                    new PcbPlacer(
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.PcbPlacementHandler],
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.PcbPlacementHandler],
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbPlacementHandler,
                        provider.GetRequiredService<IPcbSupplyHandoff>(),
                        ConveyorStation.CreatePcbPlacement(provider.GetRequiredService<IIoService>()),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>()))
            .AddSingleton(
                provider =>
                    new BoltFasteningStation(
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                        provider.GetRequiredService<IIoService>(),
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.BoltFastening],
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.BoltFastening],
                        settings.BoltFastening,
                        settings.CarrierReference,
                        ConveyorStation.CreateBoltFastening(provider.GetRequiredService<IIoService>()),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>(),
                        provider.GetRequiredService<BoltFeederUnit>(),
                        provider.GetRequiredService<ILogger<BoltFasteningStation>>()));

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
                provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>().ToDictionary(
                    pair => pair.Key, pair => new MotionStatus(pair.Value)))
            .AddSingleton<MachineController>()
            .AddSingleton<IPcbSupplyHandoff>(provider => provider.GetRequiredService<PcbSupplier>())
            .AddSingleton<BoltFeederUnit>()
            .AddSingleton<NgCarrierConveyor>()
            .AddSingleton(provider =>
            {
                var motion = provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.InspectionGantry];
                return new InspectionStation(
                    ConveyorStation.CreateInspection(provider.GetRequiredService<IIoService>()),
                    motion,
                    provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.InspectionGantry],
                    provider.GetRequiredService<NgCarrierConveyor>(),
                    provider.GetRequiredService<InspectionGantrySettings>(),
                    provider.GetRequiredService<NgCarrierTransferSettings>(),
                    provider.GetRequiredService<IIoService>(),
                    provider.GetRequiredService<UnitSettings>(),
                    provider.GetRequiredService<ICamera>(),
                    provider.GetRequiredService<ILightController>(),
                    provider.GetRequiredService<LightingSettings>(),
                    provider.GetRequiredService<RecipeManager>(),
                    provider.GetRequiredService<ILogger<InspectionStation>>());
            });

        services
            .AddSingleton<RecipeEditorViewModel>()
            .AddSingleton<MachineDiagramMapper>()
            .AddSingleton<OperationViewModel>()
            .AddSingleton<BoltStationTestViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<ManualHardwareViewModel>()
            .AddSingleton<MotionDiagnosticsViewModel>()
            .AddSingleton<TeachingViewModel>()
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
            services.AddKeyedSingleton<IXyMotion>(
                motionHardware.Group,
                (provider, _) => new AjinMotionService(
                    provider.GetRequiredService<AjinController>(),
                    motionHardware.GetAxis(MotionAxis.X)!,
                    motionHardware.GetAxis(MotionAxis.Y),
                    motionHardware.GetAxis(MotionAxis.Z),
                    motionSettings,
                    provider.GetRequiredService<MachineOptions>(),
                    provider.GetRequiredService<OperationCancellation>(),
                    provider.GetRequiredService<ILogger<AjinMotionService>>()));
        }

        return services;
    }
}
