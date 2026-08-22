using System;
using System.Collections.Generic;
using System.Linq;
using IBTM.Ajin;
using IBTM.AlphaMotion;
using IBTM.BoltFastening;
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
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public static class DependencyInjection
{
    public static IServiceCollection AddIbtmApplication(
        this IServiceCollection services,
        MachineSettings settings)
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
        services.AddSingleton(settings.Processes);
        services.AddSingleton(settings.Options);
        services.AddSingleton(settings.Home);
        services.AddSingleton(settings.PcbBuffer);
        services.AddSingleton(settings.PcbSupply);
        services.AddSingleton(settings.PcbPlacementHandler);
        services.AddSingleton(settings.BoltFastening);
        services.AddSingleton(settings.InspectionGantry);
        services.AddSingleton(settings.Lighting);
        services.AddSingleton<OperationCancellation>();
        services.AddSingleton<Recipe>();

        AddControlHardware(services, settings);
        services.AddSingleton<MainConveyor>();
        services.AddSingleton<NgConveyorLine>();
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
                supply,
                placement,
                settings.PcbSupply.BufferHandoffPosition,
                settings.PcbPlacementHandler.BufferHandoffPosition);
        });
        AddBoltHardware(services, settings);
        AddCameraHardware(services, settings);
        AddLightHardware(services, settings);
        services.AddSingleton(provider => new PcbSupplyHandler(
            provider.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply),
            provider.GetRequiredService<IIoService>(),
            settings.PcbSupply));
        services.AddSingleton(provider => new PcbPlacementHandler(
            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler),
            provider.GetRequiredService<IIoService>(),
            settings.PcbPlacementHandler));
        services.AddSingleton(provider => new BoltFasteningStation(
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening),
            settings.BoltFastening));
        services.AddSingleton<MachineState>();
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton<MachineController>();
        services.AddSingleton<PcbSupplyProcess>();
        services.AddSingleton<PcbPlacementProcess>();
        services.AddSingleton<BoltFasteningProcess>();
        services.AddSingleton<RecipeEditor>();
        services.AddSingleton<OperationViewModel>();
        services.AddSingleton<SupplyTeachingViewModel>();
        services.AddSingleton<SettingsViewModel>();
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
            services.AddSingleton<VirtualMachine>();
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

        AddAxisMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.PcbSupply,
            settings.PcbSupply.Motion,
            settings.PcbSupplyHardware,
            MachineAxis.PcbSupplyX,
            MachineAxis.PcbSupplyY,
            MachineAxis.PcbSupplyZ);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.PcbPlacementHandler,
            settings.PcbPlacementHandler.Motion,
            settings.PcbPlacementHandlerHardware,
            MachineAxis.PcbPlacementHandlerX,
            MachineAxis.PcbPlacementHandlerY,
            MachineAxis.PcbPlacementHandlerZ);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.BoltFastening,
            settings.BoltFastening.Motion,
            settings.BoltFasteningHardware,
            MachineAxis.BoltFasteningX,
            MachineAxis.BoltFasteningY,
            MachineAxis.BoltFasteningZ);
        AddXyMotion(
            services,
            settings.Drivers.Control,
            MotionGroup.InspectionGantry,
            settings.InspectionGantry.Motion,
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
            services.AddKeyedSingleton<ICamera>(
                CameraRole.Alignment,
                (_, _) => new VirtualCamera(CameraRole.Alignment));
            services.AddKeyedSingleton<ICamera>(
                CameraRole.Inspection,
                (provider, _) => new VirtualCamera(
                    CameraRole.Inspection,
                    provider.GetRequiredKeyedService<IXyMotion>(
                        MotionGroup.InspectionGantry).GetPosition));
            return;
        }

        services.AddKeyedSingleton<ICamera>(
            CameraRole.Alignment,
            (_, _) => new HikCamera(settings.AlignmentCamera));
        services.AddKeyedSingleton<ICamera>(
            CameraRole.Inspection,
            (_, _) => new HikCamera(settings.InspectionCamera));
    }

    private static void AddBoltHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        services.AddSingleton<AdcBus>();

        if (settings.Drivers.Bolt == BoltDriver.Virtual)
        {
            services.AddKeyedSingleton<IBoltHead, VirtualBoltHead>(
                FasteningHead.Shooting);
            services.AddKeyedSingleton<IBoltHead, VirtualBoltHead>(
                FasteningHead.Pickup);
            return;
        }

        services.AddKeyedSingleton<IBoltHead>(
            FasteningHead.Shooting,
            (provider, _) => new AdcBoltHead(
                provider.GetRequiredService<AdcBus>(),
                settings.Hantas,
                settings.Hantas.ShootingSlaveAddress));
        services.AddKeyedSingleton<IBoltHead>(
            FasteningHead.Pickup,
            (provider, _) => new AdcBoltHead(
                provider.GetRequiredService<AdcBus>(),
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

    private static void AddAxisMotion(
        IServiceCollection services,
        ControlDriver driver,
        MotionGroup group,
        MotionSettings settings,
        MotionHardwareSettings hardware,
        MachineAxis axisX,
        MachineAxis? axisY,
        MachineAxis? axisZ)
    {
        services.AddKeyedSingleton<IAxisMotion>(
            group,
            (provider, _) => CreateMotion(
                provider,
                driver,
                settings,
                hardware,
                axisX,
                axisY,
                axisZ));
    }

    private static void AddXyMotion(
        IServiceCollection services,
        ControlDriver driver,
        MotionGroup group,
        MotionSettings settings,
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
                hardware,
                axisX,
                axisY,
                axisZ));
    }

    private static IXyMotion CreateMotion(
        IServiceProvider provider,
        ControlDriver driver,
        MotionSettings settings,
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
                cancellation);
        }

        return new VirtualMotionService(
            settings,
            cancellation,
            hasY: y is not null,
            hasZ: z is not null,
            xRange: (x.Minimum, x.Maximum),
            yRange: y is null ? null : (y.Minimum, y.Maximum),
            zRange: z is null ? null : (z.Minimum, z.Maximum),
            resolutionMillimeters: hardware.MillimetersPerPulse);
    }
}
