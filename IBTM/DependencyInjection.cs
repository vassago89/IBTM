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
using IBTM.Virtual;
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
                                Feedback = mapping.Value.Feedback is null
                                    ? null
                                    : new(mapping.Value.Feedback.OnInput, mapping.Value.Feedback.OffInput),
                            }));

        services
            .AddSingleton(settings.Drivers)
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

        if (settings.Drivers.Control == ControlDriver.Virtual)
        {
            services
                .AddSingleton<VirtualIoService>()
                .AddSingleton(
                    provider =>
                        new VirtualMachine(
                            provider.GetRequiredService<VirtualIoService>(),
                            [
                                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                                    MotionGroup.PcbSupply),
                                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                                    MotionGroup.PcbPlacementHandler),
                                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                                    MotionGroup.BoltFastening),
                                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                                    MotionGroup.InspectionGantry),
                            ],
                            () => provider.GetRequiredService<MachineState>().RepeatEnabled))
                .AddSingleton<IIoService>(
                    provider => provider.GetRequiredService<VirtualIoService>());
        }
        else
        {
            services
                .AddSingleton(settings.Ajin)
                .AddSingleton(settings.AlphaMotion)
                .AddSingleton<AjinController>()
                .AddSingleton<AlphaMotionController>()
                .AddSingleton<IIoService, PhysicalIoService>();
        }

        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.PcbSupply.Motion,
            settings.PcbSupplyHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.PcbPlacementHandler.Motion,
            settings.PcbPlacementHandlerHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.BoltFastening.Motion,
            settings.BoltFasteningHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.InspectionGantry.Motion,
            settings.InspectionGantryHardware);

        services
            .AddSingleton<IReadOnlyDictionary<HardwareArea, IReadOnlyDictionary<OutputIo, TeachingOutput>>>(
                new Dictionary<HardwareArea, TeachingOutput[]>
                {
                    [HardwareArea.PcbSupply] = [
                        new(OutputIo.PcbSupplyGripperClosed, HardwareArea.PcbSupply),
                        new(OutputIo.PcbSupplyIpmFixerForward, HardwareArea.PcbSupply),
                        new(OutputIo.PcbSupplyRotate, HardwareArea.PcbSupply),
                    ],
                    [HardwareArea.PcbPlacementHandler] = [
                        new(OutputIo.PcbPlacementHandlerDown, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementIpmDown, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementVacuumEjector, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementStopperUp, HardwareArea.MainConveyor),
                        new(OutputIo.PcbPlacementBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                    [HardwareArea.BoltFastening] = [
                        new(OutputIo.PickupHeadDown, HardwareArea.BoltFastening),
                        new(OutputIo.PickupTableDown, HardwareArea.BoltFastening),
                        new(OutputIo.ShootingHeadDown, HardwareArea.BoltFastening),
                        new(OutputIo.PickupHeadVacuumPump, HardwareArea.BoltFastening),
                        new(OutputIo.ShootingHeadVacuumPump, HardwareArea.BoltFastening),
                        new(OutputIo.ShootBolt, HardwareArea.BoltFastening),
                        new(OutputIo.BoltFasteningStopperUp, HardwareArea.MainConveyor),
                        new(OutputIo.BoltFasteningBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                    [HardwareArea.InspectionGantry] = [
                        new(OutputIo.NgCarrierPickupDown, HardwareArea.NgCarrierTransfer),
                        new(OutputIo.NgCarrierGripperClose, HardwareArea.NgCarrierTransfer),
                        new(OutputIo.NgShuttleDown, HardwareArea.NgShuttle),
                        new(OutputIo.InspectionStopperUp, HardwareArea.MainConveyor),
                        new(OutputIo.InspectionBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                }.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyDictionary<OutputIo, TeachingOutput>)pair.Value.ToDictionary(
                        output => output.Signal)))
            .AddSingleton<IReadOnlyDictionary<HardwareArea, IoStatus[]>>(
                provider =>
                {
                    var io = provider.GetRequiredService<IoSignals>();
                    return new Dictionary<HardwareArea, IoStatus[]>
                    {
                        [HardwareArea.PcbSupply] = [settings.PcbSupplyHardware.CreateIoStatus(io)],
                        [HardwareArea.PcbPlacementHandler] = [
                            provider.GetRequiredService<PcbPlacementWork>()
                                .Station.CreateIoStatus(HardwareArea.PcbPlacementStation, io),
                            settings.PcbPlacementHandlerHardware.CreateIoStatus(io),
                        ],
                        [HardwareArea.BoltFastening] = [
                            provider.GetRequiredService<BoltFasteningWork>()
                                .Station.CreateIoStatus(HardwareArea.BoltFasteningStation, io),
                            settings.BoltFasteningHardware.CreateIoStatus(io),
                            settings.IoBoltHardware.CreateIoStatus(io),
                            settings.BoltFeederHardware.CreateIoStatus(io),
                        ],
                        [HardwareArea.InspectionGantry] = [
                            provider.GetRequiredService<InspectionWork>()
                                .Station.CreateIoStatus(HardwareArea.InspectionStation, io),
                            settings.NgCarrierTransferHardware.CreateIoStatus(io),
                            settings.NgShuttleHardware.CreateIoStatus(io),
                        ],
                    };
                });

        services
            .AddSingleton(
                provider => new PcbPlacementWork(
                    ConveyorStation.CreatePcbPlacement(provider.GetRequiredService<IIoService>()),
                    provider.GetRequiredService<UnitSettings>()))
            .AddSingleton(
                provider => new BoltFasteningWork(
                    ConveyorStation.CreateBoltFastening(provider.GetRequiredService<IIoService>()),
                    provider.GetRequiredService<UnitSettings>()))
            .AddSingleton(
                provider => new MainConveyor(
                    provider.GetRequiredService<IIoService>(),
                    provider.GetRequiredService<ConveyorSettings>(),
                    provider.GetRequiredService<OperationCancellation>(),
                    provider.GetRequiredService<PcbPlacementWork>(),
                    provider.GetRequiredService<BoltFasteningWork>(),
                    provider.GetRequiredService<InspectionWork>(),
                    provider.GetRequiredService<UnitSettings>()));

        services
            .AddSingleton(
                provider =>
                {
                    var work = new InspectionWork(
                        provider.GetRequiredService<IIoService>(),
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.InspectionGantry],
                        settings.NgCarrierTransfer,
                        provider.GetRequiredService<UnitSettings>());
                    if (settings.Drivers.Control == ControlDriver.Virtual)
                    {
                        var machine = provider.GetRequiredService<VirtualMachine>();
                        work.Motion.Feedback.PositionChanged += (x, y, _) => machine.UpdateInspectionPosition(
                            x,
                            y,
                            settings.NgCarrierTransfer.CarrierPickupPosition,
                            settings.NgCarrierTransfer.ShuttlePlacePosition);
                    }

                    return work;
                })
            .AddSingleton<INgCarrierTransferFeedback>(
                provider => provider.GetRequiredService<InspectionWork>());

        if (settings.Drivers.Bolt == BoltDriver.Virtual)
        {
            services
                .AddKeyedSingleton<IAdcBus>(FasteningHead.Pickup,
                    (provider, _) => new VirtualAdcBus(
                        provider.GetRequiredService<IIoService>(), FasteningHead.Pickup, settings.Hantas.PickupSlaveAddress))
                .AddKeyedSingleton<IAdcBus>(FasteningHead.Shooting,
                    (provider, _) => new VirtualAdcBus(
                        provider.GetRequiredService<IIoService>(), FasteningHead.Shooting, settings.Hantas.ShootingSlaveAddress));
        }
        else
        {
            services
                .AddKeyedSingleton<IAdcBus, AdcBus>(FasteningHead.Pickup)
                .AddKeyedSingleton<IAdcBus, AdcBus>(FasteningHead.Shooting);
        }

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

        if (settings.Drivers.Camera == CameraDriver.Virtual)
        {
            services
                .AddSingleton<VirtualCamera>(
                    provider =>
                    {
                        var recipes = provider.GetRequiredService<RecipeManager>();
                        return new VirtualCamera(
                            provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.InspectionGantry].GetPosition,
                            () => recipes.Current.Pcb.BoltPoints
                                .Select(bolt => bolt.InspectionPosition).OfType<AxisPosition>(),
                            // Fixed virtual labels are independent of taught FOVs and ROIs.
                            () => [
                                new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
                                new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2"),
                            ]);
                    })
                .AddSingleton<ICamera>(provider => provider.GetRequiredService<VirtualCamera>());
        }
        else
        {
            services.AddSingleton<ICamera, HikCamera>();
        }

        if (settings.Drivers.Light == LightDriver.Virtual)
            services.AddSingleton<ILightController, VirtualLightController>();
        else
            services.AddSingleton<ILightController, MovsLightController>();

        services
            .AddSingleton(
                provider =>
                {
                    var supply = new PcbSupplier(
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.PcbSupply],
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.PcbSupply],
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbSupply,
                        provider.GetRequiredService<UnitSettings>());
                    if (settings.Drivers.Control == ControlDriver.Virtual)
                    {
                        var machine = provider.GetRequiredService<VirtualMachine>();
                        var recipes = provider.GetRequiredService<RecipeManager>();
                        supply.Feedback.PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
                            x, y, z,
                            (recipes.Current.PcbSupply.Pcb1PickPosition.X,
                                recipes.Current.PcbSupply.Pcb1PickPosition.Y,
                                recipes.Current.PcbSupply.Pcb1PickPosition.Z),
                            (recipes.Current.PcbSupply.Pcb2PickPosition.X,
                                recipes.Current.PcbSupply.Pcb2PickPosition.Y,
                                recipes.Current.PcbSupply.Pcb2PickPosition.Z),
                            settings.PcbSupply.HandoffPosition);
                    }
                    return supply;
                })
            .AddSingleton(
                provider =>
                {
                    var placement = new PcbPlacer(
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.PcbPlacementHandler],
                        provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>()[MotionGroup.PcbPlacementHandler],
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbPlacementHandler,
                        provider.GetRequiredService<IPcbSupplyHandoff>(),
                        provider.GetRequiredService<PcbPlacementWork>(),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>());
                    if (settings.Drivers.Control == ControlDriver.Virtual)
                    {
                        var machine = provider.GetRequiredService<VirtualMachine>();
                        var recipes = provider.GetRequiredService<RecipeManager>();
                        placement.Feedback.PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
                            x, y, z, settings.PcbPlacementHandler.HandoffPosition,
                            settings.PcbPlacementHandler.ReceiveZ,
                            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition,
                            recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition);
                    }
                    return placement;
                })
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
                        provider.GetRequiredService<BoltFasteningWork>(),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>(),
                        provider.GetRequiredService<ILogger<BoltFasteningStation>>()));

        services
            .AddSingleton<MachineFeedbackMonitor>()
            .AddSingleton<MachineState>()
            .AddSingleton<PcbHistory>()
            .AddSingleton<PcbDetailsViewModel>()
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
            .AddSingleton(provider => new InspectionStation(
                provider.GetRequiredService<InspectionWork>(),
                provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.InspectionGantry],
                provider.GetRequiredService<NgCarrierConveyor>(),
                provider.GetRequiredService<OperationCancellation>(),
                provider.GetRequiredService<InspectionGantrySettings>(),
                provider.GetRequiredService<NgCarrierTransferSettings>(),
                provider.GetRequiredService<IIoService>(),
                provider.GetRequiredService<UnitSettings>(),
                provider.GetRequiredService<ICamera>(),
                provider.GetRequiredService<ILightController>(),
                provider.GetRequiredService<LightingSettings>(),
                provider.GetRequiredService<RecipeManager>(),
                provider.GetRequiredService<ILogger<InspectionStation>>()));

        services
            .AddSingleton<RecipeEditor>()
            .AddSingleton<MachineMap>()
            .AddSingleton<OperationViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<ManualHardwareViewModel>()
            .AddSingleton<MotionWindowViewModel>()
            .AddSingleton<TeachingViewModel>()
            .AddSingleton<InspectionTeachingViewModel>()
            .AddSingleton<DiagnosticWindows>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>();

        return services;
    }

    private static void AddXyMotion(
        IServiceCollection services,
        ControlDriver driver,
        MotionSettings settings,
        MotionHardwareSettings hardware)
    {
        services.AddKeyedSingleton<IXyMotion>(
            hardware.Group,
            (provider, _) =>
            {
                var x = hardware.GetAxis(MotionAxis.X)!;
                var y = hardware.GetAxis(MotionAxis.Y);
                var z = hardware.GetAxis(MotionAxis.Z);
                var cancellation = provider.GetRequiredService<OperationCancellation>();
                if (driver == ControlDriver.Physical)
                {
                    return new AjinMotionService(
                        provider.GetRequiredService<AjinController>(),
                        x,
                        y,
                        z,
                        settings,
                        provider.GetRequiredService<MachineOptions>(),
                        cancellation,
                        provider.GetRequiredService<ILogger<AjinMotionService>>());
                }

                var io = provider.GetRequiredService<VirtualIoService>();
                return new VirtualMotionService(
                    settings,
                    cancellation,
                    hasY: y is not null,
                    hasZ: z is not null,
                    servoPowerOn: () => io.GetInput(InputIo.ServoMainContactorOn),
                    axisResolutionMillimeters: (
                        x.MoveUnit / x.MovePulse / 1000,
                        (y?.MoveUnit ?? 1) / (y?.MovePulse ?? 1) / 1000,
                        (z?.MoveUnit ?? 1) / (z?.MovePulse ?? 1) / 1000));
            });
    }
}
