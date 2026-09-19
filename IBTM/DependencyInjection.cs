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
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using IBTM.Virtual;
using IBTM.Storage;
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
            () => settings.PcbSupply.RotationZ,
            settings.PcbSupplyHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.PcbPlacementHandler.Motion,
            () => settings.PcbPlacementHandler.BufferHandoffPosition.Z,
            settings.PcbPlacementHandlerHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.BoltFastening.Motion,
            () => settings.BoltFastening.SafeZ,
            settings.BoltFasteningHardware);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.InspectionGantry.Motion,
            null,
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
                        new(OutputIo.PcbPlacementIpmGripperClose, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementVacuumEjector, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementHandlerRotate, HardwareArea.PcbPlacementHandler),
                        new(OutputIo.PcbPlacementStopperUp, HardwareArea.MainConveyor),
                        new(OutputIo.PcbPlacementBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                    [HardwareArea.BoltFastening] = [
                        new(OutputIo.PickupHeadDown, HardwareArea.BoltFastening),
                        new(OutputIo.ShootingHeadDown, HardwareArea.BoltFastening),
                        new(OutputIo.PickupHeadVacuumPump, HardwareArea.BoltFastening),
                        new(OutputIo.ShootingHeadVacuumPump, HardwareArea.BoltFastening),
                        new(OutputIo.ShootBolt, HardwareArea.BoltFastening),
                        new(OutputIo.BoltFasteningStopperUp, HardwareArea.MainConveyor),
                        new(OutputIo.BoltFasteningBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                    [HardwareArea.InspectionGantry] = [
                        new(OutputIo.NgCarrierPickupDown, HardwareArea.NgCarrierTransfer),
                        new(OutputIo.InspectionBackupPlateUp, HardwareArea.MainConveyor),
                    ],
                    [HardwareArea.NgCarrierTransfer] = [
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
                        ],
                        [HardwareArea.NgCarrierTransfer] = [
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
            .AddSingleton<InspectionWork>()
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
            .AddSingleton<NgCarrierTransfer>()
            .AddSingleton<NgCarrierMove>()
            .AddSingleton(
                provider =>
                {
                    var gantry = new InspectionGantry(
                        provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry),
                        provider.GetRequiredService<NgCarrierTransfer>(),
                        provider.GetRequiredService<OperationCancellation>(),
                        settings.InspectionGantry);
                    if (settings.Drivers.Control == ControlDriver.Virtual)
                    {
                        var machine = provider.GetRequiredService<VirtualMachine>();
                        gantry.Feedback.PositionChanged += (x, y, _) => machine.UpdateInspectionPosition(
                            x,
                            y,
                            settings.NgCarrierTransfer.GetCarrierPickupPosition(),
                            settings.NgCarrierTransfer.ShuttlePlacePosition);
                    }

                    return gantry;
                })
            .AddSingleton<INgCarrierTransferFeedback>(
                provider => provider.GetRequiredService<NgCarrierTransfer>());
        services.AddSingleton(
            provider =>
            {
                var supply = provider.GetRequiredService<PcbSupplyHandler>();
                var placement = provider.GetRequiredService<PcbPlacementHandler>();
                if (settings.Drivers.Control == ControlDriver.Virtual)
                {
                    var machine = provider.GetRequiredService<VirtualMachine>();
                    var recipes = provider.GetRequiredService<RecipeManager>();
                    supply.Feedback.PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
                        x,
                        y,
                        z,
                        settings.PcbSupply.CarrierY,
                        (
                            recipes.Current.PcbSupply.Pcb1PickPosition.X,
                            recipes.Current.PcbSupply.Pcb1PickPosition.Z),
                        (
                            recipes.Current.PcbSupply.Pcb2PickPosition.X,
                            recipes.Current.PcbSupply.Pcb2PickPosition.Z),
                        settings.PcbSupply.BufferHandoffPosition);
                    placement.Feedback.PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
                        x,
                        y,
                        z,
                        settings.PcbPlacementHandler.BufferHandoffPosition,
                        recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition,
                        recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition);
                }

                return new BufferStage(
                    supply,
                    placement,
                    supply.Motion,
                    placement.Motion,
                    settings.PcbSupply.BufferHandoffPosition,
                    settings.PcbPlacementHandler.BufferHandoffPosition,
                    settings.Units);
            });

        if (settings.Drivers.Bolt == BoltDriver.Io)
        {
            services
                .AddKeyedSingleton<IBoltHead>(
                    FasteningHead.Pickup,
                    (provider, _) => new IoBoltHead(
                        provider.GetRequiredService<IIoService>(), FasteningHead.Pickup, settings.IoBoltHardware))
                .AddKeyedSingleton<IBoltHead>(
                    FasteningHead.Shooting,
                    (provider, _) => new IoBoltHead(
                        provider.GetRequiredService<IIoService>(), FasteningHead.Shooting, settings.IoBoltHardware));
        }
        else
        {
            if (settings.Drivers.Bolt == BoltDriver.Virtual)
                services.AddSingleton<IAdcBus, VirtualAdcBus>();
            else
                services.AddSingleton<IAdcBus, AdcBus>();

            services
                .AddKeyedSingleton<IBoltHead>(
                    FasteningHead.Shooting,
                    (provider, _) => new AdcBoltHead(
                        provider.GetRequiredService<IAdcBus>(),
                        settings.Hantas,
                        settings.Hantas.ShootingSlaveAddress))
                .AddKeyedSingleton<IBoltHead>(
                    FasteningHead.Pickup,
                    (provider, _) => new AdcBoltHead(
                        provider.GetRequiredService<IAdcBus>(),
                        settings.Hantas,
                        settings.Hantas.PickupSlaveAddress));
        }

        if (settings.Drivers.Camera == CameraDriver.Virtual)
        {
            services
                .AddSingleton<VirtualCamera>(
                    provider =>
                    {
                        var recipes = provider.GetRequiredService<RecipeManager>();
                        return new VirtualCamera(
                            provider.GetRequiredService<InspectionGantry>().Feedback.GetPosition,
                            () => recipes.Current.Pcb.BoltPoints
                                .Where(bolt => settings.CarrierReference.IsDefined
                                    && bolt.X is not null && bolt.Y is not null)
                                .Select(
                                    bolt => settings.InspectionGantry.GetBoltPosition(
                                        bolt,
                                        settings.CarrierReference)),
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
            .AddSingleton<BoltInspector>()
            .AddSingleton(
                provider =>
                    new PcbSupplyHandler(
                        provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply),
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbSupply))
            .AddSingleton(
                provider =>
                    new PcbPlacementHandler(
                        provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler),
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbPlacementHandler))
            .AddSingleton(
                provider =>
                    new BoltFasteningGantry(
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                        provider.GetRequiredService<IIoService>(),
                        provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening),
                        settings.BoltFastening,
                        settings.CarrierReference));

        services
            .AddSingleton<MachineFeedbackMonitor>()
            .AddSingleton<MachineState>()
            .AddSingleton<MachineController>()
            .AddSingleton<PcbSupplier>()
            .AddSingleton<PcbPlacer>()
            .AddSingleton<PickupBoltFeeder>()
            .AddSingleton<ShootingBoltFeeder>()
            .AddSingleton<BoltFasteningStation>()
            .AddSingleton<NgShuttleFeedback>()
            .AddSingleton<NgCarrierConveyor>();

        services
            .AddSingleton<NgShuttle>()
            .AddSingleton<InspectionStation>();

        services
            .AddSingleton<RecipeEditor>()
            .AddSingleton<MachineMap>()
            .AddSingleton<OperationViewModel>()
            .AddSingleton<SettingsViewModel>()
            .AddSingleton<ManualHardwareViewModel>()
            .AddSingleton<MotionWindowViewModel>()
            .AddSingleton<TeachingViewModel>()
            .AddSingleton<DiagnosticWindows>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>();

        return services;
    }

    private static void AddXyMotion(
        IServiceCollection services,
        ControlDriver driver,
        MotionSettings settings,
        Func<double>? horizontalZ,
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
                        horizontalZ);
                }

                var io = provider.GetRequiredService<VirtualIoService>();
                return new VirtualMotionService(
                    settings,
                    cancellation,
                    hasY: y is not null,
                    hasZ: z is not null,
                    horizontalZ: horizontalZ,
                    servoPowerOn: () => io.GetInput(InputIo.ServoMainContactorOn),
                    axisResolutionMillimeters: (
                        x.MoveUnit / x.MovePulse / 1000,
                        (y?.MoveUnit ?? 1) / (y?.MovePulse ?? 1) / 1000,
                        (z?.MoveUnit ?? 1) / (z?.MovePulse ?? 1) / 1000));
            });
    }
}
