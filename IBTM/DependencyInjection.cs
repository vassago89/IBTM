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
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(
        this IServiceCollection services,
        MachineSettings settings,
        Recipe? recipe = null)
    {
        var hardware = settings.HardwareSections;
        services.AddSingleton<IReadOnlyDictionary<InputIo, int>>(
            hardware
                .OfType<InputHardwareSettings>()
                .SelectMany(section => section.Inputs)
                .ToDictionary());
        services.AddSingleton<IReadOnlyDictionary<OutputIo, OutputHardware>>(
            hardware
                .OfType<IoHardwareSettings>()
                .SelectMany(section => section.Outputs)
                .ToDictionary(
                    mapping => mapping.Key,
                    mapping => new OutputHardware
                    {
                        Number = mapping.Value.Number,
                        OffNumber = mapping.Value.OffNumber,
                        Feedback = mapping.Value.Feedback is null
                            ? null
                            : new(
                                mapping.Value.Feedback.OnInput,
                                mapping.Value.Feedback.OffInput),
                    }));
        services.AddSingleton<IReadOnlyDictionary<MachineAxis, AxisHardware>>(
            hardware
                .OfType<MotionHardwareSettings>()
                .SelectMany(section => section.Axes)
                .ToDictionary(
                    mapping => mapping.Key,
                    mapping => new AxisHardware
                    {
                        Number = mapping.Value.Number,
                        Direction = mapping.Value.Direction,
                        Minimum = mapping.Value.Minimum,
                        Maximum = mapping.Value.Maximum,
                    }));
        services.AddSingleton(settings.Drivers);
        services.AddSingleton(settings.Units.Snapshot());
        services.AddSingleton(settings.Options);
        services.AddSingleton(settings.Home);
        services.AddSingleton(settings.RecipeSelection);
        services.AddSingleton(settings.CarrierReference);
        services.AddSingleton(settings.PcbBuffer);
        services.AddSingleton(settings.PcbSupply);
        services.AddSingleton(settings.PcbPlacementHandler);
        services.AddSingleton(settings.BoltFeeder);
        services.AddSingleton(settings.BoltFastening);
        services.AddSingleton(settings.InspectionGantry);
        services.AddSingleton(settings.NgConveyor);
        services.AddSingleton(settings.BoltInspection);
        services.AddSingleton(settings.Lighting);
        services.AddSingleton(settings.InspectionCamera);
        services.AddSingleton<OperationCancellation>();
        services.AddSingleton(recipe ?? new Recipe());

        AddControlHardware(services, settings);
        services.AddSingleton<PcbPlacementWork>();
        services.AddSingleton<BoltFasteningWork>();
        services.AddSingleton(provider => new InspectionWork(
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<UnitSettings>().NgConveyor
                ? provider.GetRequiredService<IInspectionGantryClearance>()
                : null));
        services.AddSingleton<BoltPresenceInspector>();
        services.AddSingleton<TinyUnetTrainer>();
        services.AddSingleton<BoltTrainingSession>();
        services.AddSingleton(provider =>
        {
            var units = provider.GetRequiredService<UnitSettings>();
            return new MainConveyor(
                provider.GetRequiredService<IIoService>(),
                provider.GetRequiredService<OperationCancellation>(),
                provider.GetRequiredService<PcbPlacementWork>(),
                provider.GetRequiredService<BoltFasteningWork>(),
                provider.GetRequiredService<InspectionWork>(),
                placementEnabled: units.PcbPlacement,
                boltFasteningEnabled: units.BoltFastening,
                inspectionEnabled: units.Inspection,
                inspectionBypassToNg:
                    !units.Inspection && units.NgConveyor);
        });
        services.AddSingleton(provider =>
        {
            var motion = provider.GetRequiredKeyedService<IXyMotion>(
                MotionGroup.InspectionGantry);
            if (settings.Drivers.Control == ControlDriver.Virtual)
            {
                var machine = provider.GetRequiredService<VirtualMachine>();
                motion.PositionChanged += (x, y, _) =>
                    machine.UpdateInspectionPosition(
                        x,
                        y,
                        settings.NgConveyor.CarrierPickupPosition,
                        settings.NgConveyor.ShuttlePlacePosition);
            }

            return new NgCarrierTransfer(
                provider.GetRequiredService<IIoService>(),
                motion,
                settings.NgConveyor);
        });
        services.AddSingleton<IInspectionGantryClearance>(provider =>
            provider.GetRequiredService<NgCarrierTransfer>());
        services.AddSingleton(provider =>
        {
            var units = provider.GetRequiredService<UnitSettings>();
            return new NgConveyorLine(
                provider.GetRequiredService<IIoService>(),
                provider.GetRequiredService<InspectionWork>(),
                provider.GetRequiredService<NgCarrierTransfer>(),
                settings.NgConveyor,
                !units.Inspection && units.NgConveyor);
        });
        services.AddSingleton(provider =>
        {
            var supply = provider.GetRequiredKeyedService<IAxisMotion>(
                MotionGroup.PcbSupply);
            var placement = provider.GetRequiredKeyedService<IXyMotion>(
                MotionGroup.PcbPlacementHandler);
            if (settings.Drivers.Control == ControlDriver.Virtual)
            {
                var machine = provider.GetRequiredService<VirtualMachine>();
                var recipe = provider.GetRequiredService<Recipe>();
                supply.PositionChanged += (x, y, z) =>
                    machine.UpdateSupplyPosition(
                        x,
                        y,
                        z,
                        settings.PcbSupply.CarrierY,
                        (recipe.PcbSupply.Pcb1PickPosition.X,
                            recipe.PcbSupply.Pcb1PickPosition.Z),
                        (recipe.PcbSupply.Pcb2PickPosition.X,
                            recipe.PcbSupply.Pcb2PickPosition.Z),
                        settings.PcbSupply.BufferHandoffPosition);
                placement.PositionChanged += (x, y, z) =>
                    machine.UpdatePlacementPosition(
                        x,
                        y,
                        z,
                        settings.PcbPlacementHandler.BufferHandoffPosition);
            }

            return new BufferStage(
                settings.PcbBuffer,
                provider.GetRequiredService<IIoService>(),
                provider.GetRequiredService<IBufferPlacementState>(),
                supply,
                placement,
                settings.PcbSupply.BufferHandoffPosition,
                settings.PcbPlacementHandler.BufferHandoffPosition,
                () => settings.PcbPlacementHandler.BufferEntryZ);
        });
        AddBoltHardware(services, settings);
        AddCameraHardware(services, settings);
        AddLightHardware(services, settings);
        services.AddSingleton(provider => new BoltImageCapture(
            provider.GetRequiredKeyedService<IXyMotion>(
                MotionGroup.InspectionGantry),
            provider.GetRequiredService<ICamera>(),
            provider.GetRequiredService<ILightController>(),
            settings.InspectionGantry,
            settings.CarrierReference,
            settings.Lighting));
        services.AddSingleton(provider => new BoltTrainingViewModel(
            settings.BoltInspection,
            provider.GetRequiredService<TinyUnetTrainer>(),
            provider.GetRequiredService<IBoltRecessSegmenter>(),
            provider.GetRequiredService<BoltTrainingSession>(),
            provider.GetRequiredService<BoltImageCapture>(),
            provider.GetRequiredService<InspectionWork>(),
            provider.GetRequiredService<UnitSettings>().Inspection,
            () => provider.GetRequiredService<Recipe>()
                .BoltFastening.BoltPoints));
        services.AddSingleton(provider => new PcbSupplyHandler(
            provider.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply),
            provider.GetRequiredService<IIoService>(),
            settings.PcbSupply));
        services.AddSingleton(provider => new PcbPlacementHandler(
            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler),
            provider.GetRequiredService<IIoService>(),
            settings.PcbPlacementHandler));
        services.AddSingleton<IBufferPlacementState>(provider =>
            provider.GetRequiredService<PcbPlacementHandler>());
        services.AddSingleton(provider => new BoltFasteningStation(
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening),
            settings.BoltFastening,
            settings.CarrierReference));
        services.AddSingleton<MachineState>();
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton<MachineController>();
        services.AddSingleton<PcbSupplyProcess>();
        services.AddSingleton<PcbPlacementProcess>();
        services.AddSingleton<PickupBoltFeeder>();
        services.AddSingleton<LinearBoltFeeder>();
        services.AddSingleton<BoltFasteningProcess>();
        services.AddSingleton<InspectionProcess>();
        services.AddSingleton<RecipeEditor>();
        services.AddSingleton<StartPreparation,
            PcbPlacementRecoveryPreparation>();
        services.AddSingleton<StartPreparation,
            BoltFasteningRecoveryPreparation>();
        services.AddSingleton<StartPreparationPlan>();
        services.AddSingleton<OperationViewModel>();
        services.AddSingleton<SupplyTeachingViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ManualHardwareViewModel>();
        services.AddSingleton<StationTeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }

    private static void AddControlHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.Drivers.Control == ControlDriver.Virtual)
        {
            services.AddSingleton<VirtualIoService>();
            services.AddSingleton(provider => new VirtualMachine(
                provider.GetRequiredService<VirtualIoService>(),
                [
                    (VirtualMotionService)provider
                        .GetRequiredKeyedService<IAxisMotion>(
                            MotionGroup.PcbSupply),
                    (VirtualMotionService)provider
                        .GetRequiredKeyedService<IXyMotion>(
                            MotionGroup.PcbPlacementHandler),
                    (VirtualMotionService)provider
                        .GetRequiredKeyedService<IXyMotion>(
                            MotionGroup.BoltFastening),
                    (VirtualMotionService)provider
                        .GetRequiredKeyedService<IXyMotion>(
                            MotionGroup.InspectionGantry),
                ]));
            services.AddSingleton<IIoService>(provider =>
                provider.GetRequiredService<VirtualIoService>());
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
                settings.PcbSupplyHardware,
                MachineAxis.PcbSupplyX,
                MachineAxis.PcbSupplyY,
                MachineAxis.PcbSupplyZ));
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.PcbPlacementHandler,
            settings.PcbPlacementHandler.Motion,
            () => settings.PcbPlacementHandler.BufferEntryZ,
            settings.PcbPlacementHandlerHardware,
            MachineAxis.PcbPlacementHandlerX,
            MachineAxis.PcbPlacementHandlerY,
            MachineAxis.PcbPlacementHandlerZ);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.BoltFastening,
            settings.BoltFastening.Motion,
            () => settings.BoltFastening.SafeZ,
            settings.BoltFasteningHardware,
            MachineAxis.BoltFasteningX,
            MachineAxis.BoltFasteningY,
            MachineAxis.BoltFasteningZ);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.InspectionGantry,
            settings.InspectionGantry.Motion,
            null,
            settings.InspectionGantryHardware,
            MachineAxis.InspectionGantryX,
            MachineAxis.InspectionGantryY,
            null);
    }

    private static void AddCameraHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.Drivers.Camera == CameraDriver.Virtual)
        {
            services.AddSingleton<ICamera>(provider => new VirtualCamera(
                provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.InspectionGantry).GetPosition,
                () => provider.GetRequiredService<Recipe>()
                    .BoltFastening.BoltPoints
                    .Where(bolt => bolt.X is not null && bolt.Y is not null)
                    .Select(bolt => settings.InspectionGantry.GetBoltPosition(
                        bolt,
                        settings.CarrierReference))));
            services.AddSingleton<IBoltRecessSegmenter,
                VirtualBoltRecessSegmenter>();
            return;
        }

        services.AddSingleton<ICamera>(_ =>
            new HikCamera(settings.InspectionCamera));
        services.AddSingleton<IBoltRecessSegmenter,
            TorchBoltRecessSegmenter>();
    }

    private static void AddBoltHardware(
        IServiceCollection services,
        MachineSettings settings)
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

    private static void AddLightHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.Drivers.Control == ControlDriver.Virtual)
        {
            services.AddSingleton<ILightController, VirtualLightController>();
            return;
        }

        services.AddSingleton<ILightController>(_ =>
            new MovsLightController(settings.Lighting.Connection));
    }

    private static void AddXyMotion(
        IServiceCollection services,
        ControlDriver driver,
        MotionGroup group,
        MotionSettings settings,
        Func<double>? horizontalZ,
        MotionHardwareSettings hardware,
        MachineAxis axisX,
        MachineAxis? axisY,
        MachineAxis? axisZ)
    {
        services.AddKeyedSingleton<IXyMotion>(
            group,
            (provider, _) => CreateMotion(
                provider,
                driver,
                settings,
                horizontalZ,
                hardware,
                axisX,
                axisY,
                axisZ));
    }

    private static IXyMotion CreateMotion(
        IServiceProvider provider,
        ControlDriver driver,
        MotionSettings settings,
        Func<double>? horizontalZ,
        MotionHardwareSettings hardware,
        MachineAxis axisX,
        MachineAxis? axisY,
        MachineAxis? axisZ)
    {
        var x = hardware.Axes[axisX];
        var y = axisY is null ? null : hardware.Axes[axisY.Value];
        var z = axisZ is null ? null : hardware.Axes[axisZ.Value];
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
                cancellation,
                horizontalZ);
        }

        return new VirtualMotionService(
            settings,
            cancellation,
            hasY: y is not null,
            hasZ: z is not null,
            xRange: (x.Minimum, x.Maximum),
            yRange: y is null ? null : (y.Minimum, y.Maximum),
            zRange: z is null ? null : (z.Minimum, z.Maximum),
            resolutionMillimeters: hardware.MillimetersPerPulse,
            horizontalZ: horizontalZ,
            servoPowerOn: () => provider
                .GetRequiredService<VirtualIoService>()
                .GetInput(InputIo.ServoMainContactorOn));
    }
}
