using System;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hik;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
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
        services.AddSingleton(settings.Hardware);
        services.AddSingleton(settings.Inspection);
        services.AddSingleton(settings.Lighting);
        services.AddSingleton<Recipe>();
        services.AddSingleton(_ => new BufferStage(settings.PcbBuffer));

        if (settings.ControlDriver == ControlDriver.Virtual)
        {
            AddVirtualHardware(services, settings);
        }
        else
        {
            AddAjinHardware(services, settings);
        }
        services.AddKeyedSingleton<IBoltHead, VirtualBoltService>(FasteningHead.Standard);
        services.AddKeyedSingleton<IBoltHead, VirtualBoltService>(FasteningHead.Loctite);
        AddCameraHardware(services, settings);
        AddLightHardware(services, settings);
        services.AddSingleton(provider => new PcbSupplyHandler(
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.PcbSupply),
            provider.GetRequiredService<IIoService>(),
            settings.PcbSupply));
        services.AddSingleton(provider => new PcbPlacementStation(
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.PcbPlacement),
            provider.GetRequiredKeyedService<ICamera>(CameraRole.Alignment),
            provider.GetRequiredService<IIoService>(),
            settings.PcbPlacement));
        services.AddSingleton(provider => new BoltFasteningStation(
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.BoltFastening),
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Standard),
            provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Loctite),
            provider.GetRequiredService<IIoService>(),
            settings.BoltFastening));
        services.AddSingleton(provider => new InspectionStation(
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.Inspection),
            provider.GetRequiredService<InspectionSettings>(),
            provider.GetRequiredKeyedService<ICamera>(CameraRole.Inspection),
            provider.GetRequiredService<IIoService>()));
        services.AddSingleton<EquipmentState>();
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton(provider => new[]
        {
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.PcbSupply),
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.PcbPlacement),
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.BoltFastening),
            provider.GetRequiredKeyedService<MotionService>(MotionGroup.Inspection),
        });
        services.AddSingleton<EquipmentService>();
        services.AddSingleton<PcbBufferService>();
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SupplyTeachingViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<StationTeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }

    private static void AddVirtualHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        (double Minimum, double Maximum) Range(MachineAxis axis) =>
            (
                settings.Hardware.AxisMinimums[axis],
                settings.Hardware.AxisMaximums[axis]);

        var resolution = settings.Hardware.MillimetersPerPulse;

        services.AddKeyedSingleton<MotionService>(
            MotionGroup.PcbSupply,
            (_, _) => new VirtualMotionService(
                settings.PcbSupply.Motion,
                hasY: false,
                xRange: Range(MachineAxis.PcbSupplyX),
                zRange: Range(MachineAxis.PcbSupplyZ),
                resolutionMillimeters: resolution));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.PcbPlacement,
            (_, _) => new VirtualMotionService(
                settings.PcbPlacement.Motion,
                xRange: Range(MachineAxis.PcbPlacementX),
                yRange: Range(MachineAxis.PcbPlacementY),
                zRange: Range(MachineAxis.PcbPlacementZ),
                resolutionMillimeters: resolution));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.BoltFastening,
            (_, _) => new VirtualMotionService(
                settings.BoltFastening.Motion,
                xRange: Range(MachineAxis.BoltFasteningX),
                yRange: Range(MachineAxis.BoltFasteningY),
                zRange: Range(MachineAxis.BoltFasteningZ),
                resolutionMillimeters: resolution));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.Inspection,
            (_, _) => new VirtualMotionService(
                settings.Inspection.Motion,
                xRange: Range(MachineAxis.InspectionX),
                yRange: Range(MachineAxis.InspectionY),
                zRange: Range(MachineAxis.InspectionZ),
                resolutionMillimeters: resolution));
        services.AddSingleton<VirtualIoService>();
        services.AddSingleton<IIoService>(provider => provider.GetRequiredService<VirtualIoService>());
        services.AddSingleton<IConveyorServo, VirtualConveyorServo>();
    }

    private static void AddCameraHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.CameraDriver == CameraDriver.Virtual)
        {
            services.AddKeyedSingleton<ICamera>(
                CameraRole.Alignment,
                (_, _) => new VirtualCamera(CameraRole.Alignment));
            services.AddKeyedSingleton<ICamera>(
                CameraRole.Inspection,
                (_, _) => new VirtualCamera(CameraRole.Inspection));
            return;
        }

        services.AddKeyedSingleton<ICamera>(
            CameraRole.Alignment,
            (_, _) => new HikCamera(settings.AlignmentCamera));
        services.AddKeyedSingleton<ICamera>(
            CameraRole.Inspection,
            (_, _) => new HikCamera(settings.InspectionCamera));
    }

    private static void AddLightHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.ControlDriver == ControlDriver.Virtual)
        {
            services.AddSingleton<ILightController, VirtualLightController>();
            return;
        }

        services.AddSingleton<ILightController>(_ =>
            new MovsLightController(settings.Lighting.Connection));
    }

    private static void AddAjinHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        services.AddSingleton(settings.Ajin);
        services.AddSingleton<AjinController>();
        services.AddSingleton<IIoService, AjinIoService>();
        services.AddSingleton<IConveyorServo, AjinConveyorServo>();
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.PcbSupply,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.PcbSupplyX,
                null,
                MachineAxis.PcbSupplyZ,
                settings.PcbSupply.Motion));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.PcbPlacement,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.PcbPlacementX,
                MachineAxis.PcbPlacementY,
                MachineAxis.PcbPlacementZ,
                settings.PcbPlacement.Motion));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.BoltFastening,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.BoltFasteningX,
                MachineAxis.BoltFasteningY,
                MachineAxis.BoltFasteningZ,
                settings.BoltFastening.Motion));
        services.AddKeyedSingleton<MotionService>(
            MotionGroup.Inspection,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.InspectionX,
                MachineAxis.InspectionY,
                MachineAxis.InspectionZ,
                settings.Inspection.Motion));
    }

    private static AjinMotionService CreateAjinMotion(
        IServiceProvider provider,
        MachineAxis axisX,
        MachineAxis? axisY,
        MachineAxis axisZ,
        MotionSettings settings) =>
        new(
            provider.GetRequiredService<AjinController>(),
            provider.GetRequiredService<HardwareMap>(),
            axisX,
            axisY,
            axisZ,
            settings);
}
