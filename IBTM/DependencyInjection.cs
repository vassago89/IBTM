using System;
using System.Collections.Generic;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hik;
using IBTM.PcbSupply;
using IBTM.Sequence;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
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
        services.AddSingleton(settings.PcbSupply);
        services.AddSingleton(settings.PcbPlacement);
        services.AddSingleton(settings.Conveyor);
        services.AddSingleton(settings.BoltFastening);
        services.AddSingleton(settings.Inspection);
        services.AddSingleton(settings.Lighting);

        if (settings.Driver == HardwareDriver.Virtual)
        {
            AddVirtualHardware(services, settings);
        }
        else
        {
            AddAjinHardware(services, settings);
        }

        services.AddSingleton<Conveyor>();

        services.AddKeyedSingleton<IBoltHead, VirtualBoltService>(BoltType.Standard);
        services.AddKeyedSingleton<IBoltHead, VirtualBoltService>(BoltType.Loctite);
        AddCameraHardware(services, settings);
        AddLightHardware(services, settings);
        services.AddSingleton<ProcessEvents>();
        services.AddSingleton<PcbHandoff>();
        services.AddSingleton(provider => new PcbAligner(
            provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.PcbPlacement),
            provider.GetRequiredService<PcbPlacementSettings>(),
            provider.GetRequiredKeyedService<ICameraStreamService>(EquipmentUnit.PcbPlacement),
            provider.GetRequiredService<ILightController>(),
            provider.GetRequiredService<LightingSettings>()));
        services.AddSingleton(provider => new PcbFeeder(
            provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.PcbSupply),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<PcbHandoff>(),
            provider.GetRequiredService<PcbSupplySettings>(),
            provider.GetRequiredService<ProcessEvents>()));
        services.AddSingleton(provider => new PcbPlacementStation(
            provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.PcbPlacement),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<PcbHandoff>(),
            provider.GetRequiredService<PcbPlacementSettings>(),
            provider.GetRequiredService<ProcessEvents>(),
            provider.GetRequiredService<PcbAligner>()));
        services.AddSingleton(provider => new BoltFasteningStation(
            provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.BoltFastening),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<BoltFasteningSettings>(),
            provider.GetRequiredService<ProcessEvents>(),
            provider.GetRequiredKeyedService<IBoltHead>(BoltType.Standard),
            provider.GetRequiredKeyedService<IBoltHead>(BoltType.Loctite)));
        services.AddSingleton(provider => new InspectionStation(
            provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.Inspection),
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<InspectionSettings>(),
            provider.GetRequiredService<ProcessEvents>(),
            provider.GetRequiredKeyedService<ICameraStreamService>(EquipmentUnit.Inspection),
            provider.GetRequiredService<ILightController>(),
            provider.GetRequiredService<LightingSettings>()));
        services.AddSingleton<TeachingPointMapper>();
        services.AddSingleton(provider => new AutoSequence(
            provider.GetRequiredService<IIoService>(),
            provider.GetRequiredService<Conveyor>(),
            provider.GetRequiredService<PcbFeeder>(),
            provider.GetRequiredService<PcbPlacementStation>(),
            provider.GetRequiredService<BoltFasteningStation>(),
            provider.GetRequiredService<InspectionStation>(),
            provider.GetRequiredService<ILightController>(),
            new Dictionary<EquipmentUnit, MotionService>
            {
                [EquipmentUnit.PcbSupply] =
                    provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.PcbSupply),
                [EquipmentUnit.PcbPlacement] =
                    provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.PcbPlacement),
                [EquipmentUnit.BoltFastening] =
                    provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.BoltFastening),
                [EquipmentUnit.Inspection] =
                    provider.GetRequiredKeyedService<MotionService>(EquipmentUnit.Inspection),
            },
            provider.GetRequiredService<MachineSettings>().Options,
            provider.GetRequiredService<ProcessEvents>()));
        services.AddSingleton<ProcessViewModel>();
        services.AddSingleton<SupplyTeachingViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TeachingViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }

    private static void AddVirtualHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.PcbSupply,
            (_, _) => new VirtualMotionService(
                settings.PcbSupply.Motion,
                hasY: false));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.PcbPlacement,
            (_, _) => new VirtualMotionService(settings.PcbPlacement.Motion));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.BoltFastening,
            (_, _) => new VirtualMotionService(settings.BoltFastening.Motion));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.Inspection,
            (_, _) => new VirtualMotionService(settings.Inspection.Motion));
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
            services.AddKeyedSingleton<ICameraStreamService>(
                EquipmentUnit.PcbPlacement,
                (_, _) => new VirtualCameraStreamService(inspection: false));
            services.AddKeyedSingleton<ICameraStreamService>(
                EquipmentUnit.Inspection,
                (_, _) => new VirtualCameraStreamService(inspection: true));
            return;
        }

        services.AddKeyedSingleton<ICameraStreamService>(
            EquipmentUnit.PcbPlacement,
            (_, _) => new HikCameraStreamService(settings.AlignmentCamera));
        services.AddKeyedSingleton<ICameraStreamService>(
            EquipmentUnit.Inspection,
            (_, _) => new HikCameraStreamService(settings.InspectionCamera));
    }

    private static void AddLightHardware(
        IServiceCollection services,
        MachineSettings settings)
    {
        if (settings.Driver == HardwareDriver.Virtual)
        {
            services.AddSingleton<ILightController, VirtualLightController>();
            return;
        }

        services.AddSingleton<ILightController>(
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
            EquipmentUnit.PcbSupply,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.PcbSupplyX,
                null,
                MachineAxis.PcbSupplyZ,
                settings.PcbSupply.Motion));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.PcbPlacement,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.PcbPlacementX,
                MachineAxis.PcbPlacementY,
                MachineAxis.PcbPlacementZ,
                settings.PcbPlacement.Motion));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.BoltFastening,
            (provider, _) => CreateAjinMotion(
                provider,
                MachineAxis.BoltFasteningX,
                MachineAxis.BoltFasteningY,
                MachineAxis.BoltFasteningZ,
                settings.BoltFastening.Motion));
        services.AddKeyedSingleton<MotionService>(
            EquipmentUnit.Inspection,
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
        StationMotionSettings settings) =>
        new(
            provider.GetRequiredService<AjinController>(),
            provider.GetRequiredService<HardwareMap>(),
            axisX,
            axisY,
            axisZ,
            settings);
}
