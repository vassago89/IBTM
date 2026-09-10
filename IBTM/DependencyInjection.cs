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
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.UI;
using IBTM.Virtual;
using IBTM.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IBTM;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(
        this IServiceCollection services,
        MachineSettings settings,
        Recipe? recipe = null)
    {
        services.TryAddSingleton<ApplicationLog>();
        services.TryAddSingleton(_ => new MachineStore());
        services.TryAddSingleton<RecipeStore>();
        var hardware = settings.HardwareSections;
        services.AddSingleton(settings);
        services.AddSingleton<IReadOnlyList<MotionHardwareSettings>>(
            hardware.OfType<MotionHardwareSettings>().ToArray());
        services.AddSingleton(
            provider => new IoSignals(hardware, provider.GetRequiredService<IIoService>()));
        services.AddSingleton<IReadOnlyDictionary<InputIo, int>>(
            hardware.OfType<InputHardwareSettings>()
                .SelectMany(section => section.Inputs)
                .ToDictionary());
        services.AddSingleton<IReadOnlyDictionary<OutputIo, OutputHardware>>(
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
        services.AddSingleton(settings.Drivers);
        services.AddSingleton(settings.Units);
        services.AddSingleton(settings.Options);
        services.AddSingleton(settings.RecipeSelection);
        services.AddSingleton(settings.CarrierReference);
        services.AddSingleton(settings.PcbBuffer);
        services.AddSingleton(settings.PcbSupply);
        services.AddSingleton(settings.PcbPlacementHandler);
        services.AddSingleton(settings.BoltFeeder);
        services.AddSingleton(settings.BoltFastening);
        services.AddSingleton(settings.InspectionGantry);
        services.AddSingleton(settings.NgCarrierTransfer);
        services.AddSingleton(settings.NgConveyor);
        services.AddSingleton(settings.Hantas);
        services.AddSingleton<OperationCancellation>();
        var currentRecipe = recipe ?? new Recipe();
        services.AddSingleton(currentRecipe);

        AddControlHardware(services, settings);
        services.AddSingleton<IReadOnlyDictionary<MotionGroup, IReadOnlyDictionary<OutputIo, TeachingOutput>>>(
            new Dictionary<MotionGroup, TeachingOutput[]>
            {
                [MotionGroup.PcbSupply] = [
                    new(OutputIo.PcbSupplyGripperClosed, HardwareArea.PcbSupply),
                    new(OutputIo.PcbSupplyIpmFixerForward, HardwareArea.PcbSupply),
                    new(OutputIo.PcbSupplyRotate, HardwareArea.PcbSupply),
                ],
                [MotionGroup.PcbPlacementHandler] = [
                    new(OutputIo.PcbPlacementHandlerDown, HardwareArea.PcbPlacementHandler),
                    new(OutputIo.PcbPlacementIpmDown, HardwareArea.PcbPlacementHandler),
                    new(OutputIo.PcbPlacementIpmGripperClose, HardwareArea.PcbPlacementHandler),
                    new(OutputIo.PcbPlacementVacuumEjector, HardwareArea.PcbPlacementHandler),
                    new(OutputIo.PcbPlacementHandlerRotate, HardwareArea.PcbPlacementHandler),
                    new(OutputIo.PcbPlacementBackupPlateUp, HardwareArea.MainConveyor),
                ],
                [MotionGroup.BoltFastening] = [
                    new(OutputIo.PickupHeadDown, HardwareArea.BoltFastening),
                    new(OutputIo.ShootingHeadDown, HardwareArea.BoltFastening),
                    new(OutputIo.PickupHeadVacuumPump, HardwareArea.BoltFastening),
                    new(OutputIo.ShootingHeadVacuumPump, HardwareArea.BoltFastening),
                    new(OutputIo.ShootBolt, HardwareArea.BoltFastening, HoldToRun: true),
                    new(OutputIo.BoltFasteningBackupPlateUp, HardwareArea.MainConveyor),
                ],
                [MotionGroup.InspectionGantry] = [
                    new(OutputIo.NgCarrierPickupDown, HardwareArea.NgCarrierTransfer),
                    new(OutputIo.NgCarrierGripperClose, HardwareArea.NgCarrierTransfer),
                    new(OutputIo.InspectionBackupPlateUp, HardwareArea.MainConveyor),
                ],
            }.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<OutputIo, TeachingOutput>)pair.Value.ToDictionary(
                    output => output.Signal)));
        services.AddSingleton<IReadOnlyDictionary<MotionGroup, IoStatus[]>>(
            provider =>
            {
                var io = provider.GetRequiredService<IoSignals>();
                var buffer = settings.PcbBufferHardware.CreateIoStatus(io);
                return new Dictionary<MotionGroup, IoStatus[]>
                {
                    [MotionGroup.PcbSupply] = [settings.PcbSupplyHardware.CreateIoStatus(io), buffer,],
                    [MotionGroup.PcbPlacementHandler] = [
                        provider.GetRequiredService<PcbPlacementWork>()
                            .Station.CreateIoStatus(HardwareArea.PcbPlacementStation, io),
                        settings.PcbPlacementHandlerHardware.CreateIoStatus(io),
                        buffer,
                    ],
                    [MotionGroup.BoltFastening] = [
                        provider.GetRequiredService<BoltFasteningWork>()
                            .Station.CreateIoStatus(HardwareArea.BoltFasteningStation, io),
                        settings.BoltFasteningHardware.CreateIoStatus(io),
                        settings.BoltFeederHardware.CreateIoStatus(io),
                    ],
                    [MotionGroup.InspectionGantry] = [
                        provider.GetRequiredService<InspectionWork>()
                            .Station.CreateIoStatus(HardwareArea.InspectionStation, io),
                        settings.NgCarrierTransferHardware.CreateIoStatus(io),
                        settings.NgShuttleHardware.CreateIoStatus(io),
                    ],
                };
            });
        services.AddSingleton(
            provider =>
            {
                var units = provider.GetRequiredService<UnitSettings>();
                return new PcbPlacementWork(
                    ConveyorStation.PcbPlacement(provider.GetRequiredService<IIoService>()),
                    () => units.PcbPlacement);
            });
        services.AddSingleton(
            provider =>
            {
                var units = provider.GetRequiredService<UnitSettings>();
                return new BoltFasteningWork(
                    ConveyorStation.BoltFastening(provider.GetRequiredService<IIoService>()),
                    () => units.BoltFastening);
            });
        services.AddSingleton(
            provider =>
            {
                var units = provider.GetRequiredService<UnitSettings>();
                return new InspectionWork(
                    ConveyorStation.Inspection(provider.GetRequiredService<IIoService>()),
                    provider.GetRequiredService<INgCarrierTransferFeedback>(),
                    () => units.Inspection);
            });
        services.AddSingleton(
            provider =>
            {
                var training = provider.GetRequiredService<BoltTrainingSettings>();
                return new BoltPresenceDetector(
                    () => currentRecipe.BoltInspection,
                    provider.GetRequiredService<IBoltRecessSegmenter>(),
                    () => training.MaskThreshold);
            });
        services.AddSingleton<BoltTrainingSession>();
        services.AddSingleton(
            provider =>
            {
                var units = provider.GetRequiredService<UnitSettings>();
                var inspection = provider.GetRequiredService<InspectionWork>();
                return new MainConveyor(
                    provider.GetRequiredService<IIoService>(),
                    provider.GetRequiredService<OperationCancellation>(),
                    provider.GetRequiredService<PcbPlacementWork>(),
                    provider.GetRequiredService<BoltFasteningWork>(),
                    inspection,
                    routeInspectionToNg: () => units.NgCarrierTransfer && inspection.RouteToNg);
            });
        services.AddSingleton(
            provider =>
                new MainConveyorDryRun(
                    provider.GetRequiredService<MainConveyor>(),
                    [
            provider.GetRequiredService<PcbPlacementWork>().Station,
            provider.GetRequiredService<BoltFasteningWork>().Station,
            provider.GetRequiredService<InspectionWork>().Station
        ]));
        services.AddSingleton<NgCarrierTransfer>();
        services.AddSingleton<NgCarrierMove>();
        services.AddSingleton<NgTransferDryRun>();
        services.AddSingleton<InspectionDryRun>();
        services.AddSingleton<PcbReturn>();
        services.AddSingleton<PcbDryRun>();
        services.AddSingleton<NgConveyorDryRun>();
        services.AddSingleton<BoltRouteDryRun>();
        services.AddSingleton(
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
                        settings.NgCarrierTransfer.CarrierPickupPosition,
                        settings.NgCarrierTransfer.ShuttlePlacePosition);
                }

                return gantry;
            });
        services.AddSingleton<INgCarrierTransferFeedback>(
            provider => provider.GetRequiredService<NgCarrierTransfer>());
        services.AddSingleton(
            provider =>
            {
                var supply = provider.GetRequiredService<PcbSupplyHandler>();
                var placement = provider.GetRequiredService<PcbPlacementHandler>();
                if (settings.Drivers.Control == ControlDriver.Virtual)
                {
                    var machine = provider.GetRequiredService<VirtualMachine>();
                    var recipe = provider.GetRequiredService<Recipe>();
                    supply.Feedback.PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
                        x,
                        y,
                        z,
                        settings.PcbSupply.CarrierY,
                        (
                            recipe.PcbSupply.Pcb1PickPosition.X,
                            recipe.PcbSupply.Pcb1PickPosition.Z),
                        (
                            recipe.PcbSupply.Pcb2PickPosition.X,
                            recipe.PcbSupply.Pcb2PickPosition.Z),
                        settings.PcbSupply.BufferHandoffPosition);
                    placement.Feedback.PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
                        x,
                        y,
                        z,
                        settings.PcbPlacementHandler.BufferHandoffPosition,
                        recipe.PcbPlacement.HeatSink1PcbPlacementPosition,
                        recipe.PcbPlacement.HeatSink2PcbPlacementPosition);
                }

                return new BufferStage(
                    settings.PcbBuffer,
                    provider.GetRequiredService<IIoService>(),
                    placement,
                    supply.Feedback,
                    placement.Feedback,
                    settings.PcbSupply.BufferHandoffPosition,
                    settings.PcbPlacementHandler.BufferHandoffPosition,
                    () => settings.PcbPlacementHandler.BufferEntryZ,
                    () => settings.Units.PcbSupply,
                    () => settings.Units.PcbPlacement);
            });
        services.AddSingleton<Func<PcbLayout>>(_ => () => currentRecipe.Pcb);
        AddBoltHardware(services, settings);
        AddCameraHardware(services, settings, currentRecipe);
        AddLightHardware(services, settings);
        services.AddSingleton(
            provider =>
            {
                var inspector = new BoltInspector(
                    provider.GetRequiredService<InspectionGantry>(),
                    provider.GetRequiredService<ICamera>(),
                    provider.GetRequiredService<ILightController>(),
                    provider.GetRequiredService<BoltPresenceDetector>(),
                    settings.InspectionGantry,
                    settings.CarrierReference,
                    settings.Lighting,
                    () => currentRecipe.BoltInspection,
                    () => currentRecipe.Pcb,
                    () => currentRecipe.CarrierImageMillimetersPerPixel);
                inspector.Inspected += provider.GetRequiredService<BoltImageCollector>().Collect;
                return inspector;
            });
        services.AddSingleton(_ => new BoltTrainingStore());
        services.AddSingleton(
            provider => provider.GetRequiredService<BoltTrainingStore>().LoadSettings());
        services.AddSingleton(
            provider =>
                new BoltImageCollector(
                    provider.GetRequiredService<BoltTrainingStore>(),
                    provider.GetRequiredService<BoltTrainingSettings>(),
                    () => currentRecipe.Name));
        services.AddSingleton(
            provider =>
                new BoltTrainingViewModel(
                    () => currentRecipe.BoltInspection,
                    provider.GetRequiredService<BoltTrainingStore>(),
                    provider.GetRequiredService<BoltTrainingSettings>(),
                    provider.GetRequiredService<BoltImageCollector>(),
                    provider.GetRequiredService<IBoltRecessSegmenter>(),
                    provider.GetRequiredService<BoltTrainingSession>()));
        services.AddSingleton(
            provider =>
                new PcbSupplyHandler(
                    provider.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply),
                    provider.GetRequiredService<IIoService>(),
                    settings.PcbSupply,
                    settings.PcbBuffer));
        services.AddSingleton(
            provider =>
                new PcbPlacementHandler(
                    provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler),
                    provider.GetRequiredService<IIoService>(),
                    settings.PcbPlacementHandler));
        services.AddSingleton(
            provider =>
                new BoltFasteningGantry(
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                    provider.GetRequiredService<IIoService>(),
                    provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening),
                    settings.BoltFastening,
                    settings.CarrierReference));
        services.AddSingleton<MachineState>();
        services.AddSingleton<MachineController>();
        services.AddSingleton<PcbSupplier>();
        services.AddSingleton<PcbPlacer>();
        services.AddSingleton<PickupBoltFeeder>();
        services.AddSingleton<ShootingBoltFeeder>();
        services.AddSingleton<BoltFasteningStation>();
        services.AddSingleton<NgShuttleFeedback>();
        services.AddSingleton<NgCarrierConveyor>();
        services.AddSingleton(
            provider =>
                new NgShuttle(
                    provider.GetRequiredService<IIoService>(),
                    provider.GetRequiredService<NgCarrierConveyor>(),
                    provider.GetRequiredService<NgShuttleFeedback>(),
                    provider.GetRequiredService<INgCarrierTransferFeedback>()));
        services.AddSingleton(
            provider =>
            {
                var units = provider.GetRequiredService<UnitSettings>();
                return new InspectionStation(
                    provider.GetRequiredService<InspectionWork>(),
                    provider.GetRequiredService<BoltInspector>(),
                    provider.GetRequiredService<NgCarrierTransfer>(),
                    provider.GetRequiredService<NgCarrierMove>(),
                    provider.GetRequiredService<NgShuttle>(),
                    () => units.NgCarrierTransfer);
            });
        services.AddSingleton<RecipeEditor>();
        services.AddSingleton<PcbPlacementRecoveryPreparation>();
        services.AddSingleton<BoltFasteningRecoveryPreparation>();
        services.AddSingleton<MachineMap>();
        services.AddSingleton<OperationViewModel>();
        services.AddSingleton<SupplyTeachingViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ManualHardwareViewModel>();
        services.AddSingleton<MotionWindowViewModel>();
        services.AddSingleton<StationTeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }

    private static void AddControlHardware(IServiceCollection services, MachineSettings settings)
    {
        if (settings.Drivers.Control == ControlDriver.Virtual)
        {
            services.AddSingleton<VirtualIoService>();
            services.AddSingleton(
                provider =>
                    new VirtualMachine(
                        provider.GetRequiredService<VirtualIoService>(),
                        [
                (VirtualMotionService)provider.GetRequiredKeyedService<IAxisMotion>(
                    MotionGroup.PcbSupply),
                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.PcbPlacementHandler),
                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.BoltFastening),
                (VirtualMotionService)provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.InspectionGantry),
            ]));
            services.AddSingleton<IIoService>(
                provider => provider.GetRequiredService<VirtualIoService>());
        }
        else
        {
            services.AddSingleton(settings.Ajin);
            services.AddSingleton(settings.AlphaMotion);
            services.AddSingleton<AjinController>();
            services.AddSingleton<AlphaMotionController>();
            services.AddSingleton<IIoService, PhysicalIoService>();
        }

        services.AddKeyedSingleton<IAxisMotion>(
            MotionGroup.PcbSupply,
            (provider, _) => CreateMotion(
                provider,
                settings.Drivers.Control,
                settings.PcbSupply.Motion,
                () => settings.PcbSupply.RotationZ,
                settings.PcbSupplyHardware));
        AddXyMotion(
            services,
            settings.Drivers.Control,
            settings.PcbPlacementHandler.Motion,
            () => settings.PcbPlacementHandler.BufferEntryZ,
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
    }

    private static void AddCameraHardware(
        IServiceCollection services,
        MachineSettings settings,
        Recipe recipe)
    {
        if (settings.Drivers.Camera == CameraDriver.Virtual)
        {
            services.AddSingleton<VirtualCamera>(
                provider =>
                    new VirtualCamera(
                        provider.GetRequiredService<InspectionGantry>().Feedback.GetPosition,
                        () => recipe.Pcb.GetBolts()
                            .Where(bolt => bolt.X is not null && bolt.Y is not null)
                            .Select(
                                bolt => settings.InspectionGantry.GetBoltPosition(
                                    bolt,
                                    settings.CarrierReference)),
                        () => Enum.GetValues<HeatSinkSlot>()
                            .Select(pcb => (Pcb: pcb, Region: recipe.Pcb.GetDataMatrix(pcb)))
                            .Where(item => item.Region is not null)
                            .Select(
                                item =>
                                    new VirtualDataMatrix(
                                        CarrierCoordinates.ToMachine(
                                            item.Region!.Center,
                                            settings.CarrierReference.UpperLeftLocatingPin!),
                                        item.Region.Width,
                                        item.Region.Height,
                                        item.Pcb == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2"))));
            services.AddSingleton<ICamera>(provider => provider.GetRequiredService<VirtualCamera>());
        }
        else
        {
            services.AddSingleton<ICamera>(_ => new HikCamera(settings.InspectionCamera));
        }

        if (settings.Drivers.Inspection == InspectionAlgorithm.Virtual)
        {
            services.AddSingleton<IBoltRecessSegmenter, VirtualBoltRecessSegmenter>();
        }
        else
        {
            services.AddSingleton<IBoltRecessSegmenter, TorchBoltRecessSegmenter>();
        }
    }

    private static void AddBoltHardware(IServiceCollection services, MachineSettings settings)
    {
        if (settings.Drivers.Bolt == BoltDriver.Virtual)
        {
            services.AddSingleton<IAdcBus, VirtualAdcBus>();
        }
        else
        {
            services.AddSingleton<IAdcBus, AdcBus>();
        }

        services.AddKeyedSingleton<IBoltHead>(
            FasteningHead.Shooting,
            (provider, _) => new AdcBoltHead(
                provider.GetRequiredService<IAdcBus>(),
                settings.Hantas,
                settings.Hantas.ShootingSlaveAddress));
        services.AddKeyedSingleton<IBoltHead>(
            FasteningHead.Pickup,
            (provider, _) => new AdcBoltHead(
                provider.GetRequiredService<IAdcBus>(),
                settings.Hantas,
                settings.Hantas.PickupSlaveAddress));
    }

    private static void AddLightHardware(IServiceCollection services, MachineSettings settings)
    {
        if (settings.Drivers.Light == LightDriver.Virtual)
        {
            services.AddSingleton<ILightController, VirtualLightController>();
            return;
        }

        services.AddSingleton<ILightController>(_ => new MovsLightController(settings.Lighting));
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
            (provider, _) => CreateMotion(provider, driver, settings, horizontalZ, hardware));
    }

    private static IXyMotion CreateMotion(
        IServiceProvider provider,
        ControlDriver driver,
        MotionSettings settings,
        Func<double>? horizontalZ,
        MotionHardwareSettings hardware)
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
                hardware.MillimetersPerPulse,
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
            xRange: (
                x.Minimum,
                x.Maximum),
            yRange: y is null ? null : (
                y.Minimum,
                y.Maximum),
            zRange: z is null ? null : (
                z.Minimum,
                z.Maximum),
            resolutionMillimeters: hardware.MillimetersPerPulse,
            horizontalZ: horizontalZ,
            servoPowerOn: () => io.GetInput(InputIo.ServoMainContactorOn));
    }
}
