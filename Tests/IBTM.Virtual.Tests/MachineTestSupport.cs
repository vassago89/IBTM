using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Virtual.Tests;

// Shared virtual machine setup and fault injection for lifecycle, home and teaching tests.
public static class MachineTestSupport
{
    public static IServiceCollection AddVirtualApplication(
        this IServiceCollection services,
        MachineSettings settings)
    {
        services.AddIbtmApplication(settings)
            .AddSingleton<VirtualIoService>()
            .AddSingleton(provider =>
            {
                var io = provider.GetRequiredService<VirtualIoService>();
                var motions = settings.MotionSections.ToDictionary(
                    section => section.Hardware.Group,
                    section => provider.GetRequiredKeyedService<VirtualMotionService>(section.Hardware.Group));
                var machine = new VirtualMachine(io, motions.Values.ToArray());
                var recipes = provider.GetRequiredService<RecipeManager>();
                motions[MotionGroup.PcbSupply].PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
                    x, y, z,
                    (recipes.Current.PcbSupply.Pcb1PickPosition.X,
                        recipes.Current.PcbSupply.Pcb1PickPosition.Y,
                        recipes.Current.PcbSupply.Pcb1PickPosition.Z),
                    (recipes.Current.PcbSupply.Pcb2PickPosition.X,
                        recipes.Current.PcbSupply.Pcb2PickPosition.Y,
                        recipes.Current.PcbSupply.Pcb2PickPosition.Z),
                    settings.PcbSupply.HandoffPosition);
                motions[MotionGroup.PcbPlacementHandler].PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
                    x, y, z, settings.PcbPlacementHandler.HandoffPosition,
                    settings.PcbPlacementHandler.ReceiveZ,
                    recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition,
                    recipes.Current.PcbPlacement.HeatSink2PcbPlacementPosition);
                motions[MotionGroup.InspectionGantry].PositionChanged += (x, y, _) => machine.UpdateInspectionPosition(
                    x, y, settings.NgCarrierTransfer.CarrierPickupPosition,
                    settings.NgCarrierTransfer.ShuttlePlacePosition);
                return machine;
            })
            .AddSingleton<IIoService>(provider =>
            {
                _ = provider.GetRequiredService<VirtualMachine>();
                return provider.GetRequiredService<VirtualIoService>();
            })
            .AddKeyedSingleton<IAdcBus>(FasteningHead.Pickup,
                (provider, _) => new VirtualAdcBus(
                    provider.GetRequiredService<IIoService>(), FasteningHead.Pickup, settings.Hantas.PickupSlaveAddress))
            .AddKeyedSingleton<IAdcBus>(FasteningHead.Shooting,
                (provider, _) => new VirtualAdcBus(
                    provider.GetRequiredService<IIoService>(), FasteningHead.Shooting, settings.Hantas.ShootingSlaveAddress))
            .AddSingleton<VirtualCamera>(provider =>
            {
                var recipes = provider.GetRequiredService<RecipeManager>();
                var motion = provider.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()
                    [MotionGroup.InspectionGantry];
                return new VirtualCamera(
                    () => motion.Position,
                    () => recipes.Current.Pcb.BoltPoints
                        .Select(bolt => bolt.InspectionPosition).OfType<AxisPosition>(),
                    () => [
                        new(new() { X = 13, Y = 15 }, 4, 4, "PCB-1"),
                        new(new() { X = 31, Y = 15 }, 4, 4, "PCB-2"),
                    ]);
            })
            .AddSingleton<ICamera>(provider => provider.GetRequiredService<VirtualCamera>())
            .AddSingleton<ILightController, VirtualLightController>();

        foreach (var (motionSettings, motionHardware) in settings.MotionSections)
        {
            services.AddKeyedSingleton<VirtualMotionService>(motionHardware.Group, (provider, _) =>
            {
                var x = motionHardware.GetAxis(MotionAxis.X)!;
                var y = motionHardware.GetAxis(MotionAxis.Y);
                var z = motionHardware.GetAxis(MotionAxis.Z);
                var io = provider.GetRequiredService<VirtualIoService>();
                return new VirtualMotionService(
                    motionSettings,
                    provider.GetRequiredService<OperationCancellation>(),
                    hasY: y is not null,
                    hasZ: z is not null,
                    servoPowerOn: () => io.GetInput(InputIo.ServoMainContactorOn),
                    axisResolutionMillimeters: (
                        x.MoveUnit / x.MovePulse / 1000,
                        (y?.MoveUnit ?? 1) / (y?.MovePulse ?? 1) / 1000,
                        (z?.MoveUnit ?? 1) / (z?.MovePulse ?? 1) / 1000));
            })
            .AddKeyedSingleton<IXyMotion>(motionHardware.Group,
                (provider, _) => provider.GetRequiredKeyedService<VirtualMotionService>(motionHardware.Group));
        }

        return services;
    }

    public static ServiceProvider CreateDisplayServices(
        out DisplayReadMotion feedback,
        MachineSettings? settings = null)
    {
        var motion = DispatchProxy.Create<IXyMotion, DisplayReadMotion>();
        var probe = (DisplayReadMotion)motion;
        feedback = probe;
        return new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings ?? FlowSettings())
            .AddKeyedSingleton<IXyMotion>(MotionGroup.InspectionGantry, (provider, _) =>
            {
                probe.Motion = provider.GetRequiredKeyedService<VirtualMotionService>(MotionGroup.InspectionGantry);
                return motion;
            })
            .BuildServiceProvider();
    }

    public static ServiceProvider CreateDiagnosticServices(
        ILightController? light = null,
        Action<ServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(
            VirtualTestSupport.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-diagnostic-{Guid.NewGuid():N}.db")))
            .AddVirtualApplication(
                new MachineSettings
                {
                    Units = new()
                    {
                        MainConveyor = true,
                        PcbSupply = false,
                        PcbPlacement = false,
                        PickupBoltFeeder = false,
                        ShootingBoltFeeder = false,
                        BoltFastening = false,
                        Inspection = false,
                        NgConveyor = false,
                    },
                });
        if (light is not null)
            collection.AddSingleton(light);
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    public static ServiceProvider CreateServices(MachineSettings settings)
    {
        return new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true, });
    }

    public static ServiceProvider CreateMotionScopeServices(
        MachineSettings settings,
        out Dictionary<MotionGroup, ScopedMotionProbe> probes,
        Action<IServiceCollection>? configure = null)
    {
        var captured = new Dictionary<MotionGroup, ScopedMotionProbe>();
        probes = captured;
        var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings);
        foreach (var group in Enum.GetValues<MotionGroup>())
        {
            services.AddKeyedSingleton<IXyMotion>(group, (provider, _) =>
            {
                var motion = DispatchProxy.Create<IXyMotion, ScopedMotionProbe>();
                var probe = (ScopedMotionProbe)motion;
                probe.Motion = provider.GetRequiredKeyedService<VirtualMotionService>(group);
                captured.Add(group, probe);
                return motion;
            });
        }
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        // These tests replace the handler factories that normally initialize virtual feedback.
        _ = provider.GetRequiredService<VirtualMachine>();
        return provider;
    }

    public static UnitSettings EnableOnly(MachineUnit unit)
    {
        return new()
        {
            MainConveyor = unit == MachineUnit.MainConveyor,
            PcbSupply = unit == MachineUnit.PcbSupply,
            PcbPlacement = unit == MachineUnit.PcbPlacement,
            PickupBoltFeeder = unit == MachineUnit.PickupBoltFeeder,
            ShootingBoltFeeder = unit == MachineUnit.ShootingBoltFeeder,
            BoltFastening = unit == MachineUnit.BoltFastening,
            Inspection = unit == MachineUnit.Inspection,
            NgConveyor = unit == MachineUnit.NgConveyor,
        };
    }

    public static HomeSettings FastHome()
    {
        return new() { SearchSpeed = 10_000 };
    }

    public static void FastHomes(MachineSettings settings)
    {
        foreach (var (motion, _) in settings.MotionSections)
        {
            motion.HorizontalHome = FastHome();
            motion.ZHome = FastHome();
        }
    }

    public static MachineSettings FlowSettings()
    {
        var settings = new MachineSettings();
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.TravelZ = 0;
        settings.PcbSupply.HandoffPosition = new()
        {
            X = 80,
            Y = 30,
        };
        settings.PcbPlacementHandler.Motion = FastMotion();
        settings.PcbPlacementHandler.ReceiveZ = 12;
        settings.PcbPlacementHandler.HandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.BoltFastening.Motion = FastMotion();
        settings.BoltFastening.DryRunMilliseconds = 30;
        // Virtual tube passage takes 200 ms after detection, while the blow output remains on.
        settings.BoltFastening.ShootingArrivalDelaySeconds = 0.5;
        settings.BoltFastening.SafeZ = 0;
        settings.BoltFastening.PickupPosition = new()
        {
            X = 100,
            Y = 50,
            Z = 10,
        };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 150, Y = 20 };
        return settings;
    }

    public static MotionSettings FastMotion()
    {
        return new()
        {
            HorizontalSpeed = 10_000,
            ZSpeed = 10_000,
            HorizontalHome = FastHome(),
            ZHome = FastHome(),
        };
    }

    public static BoltHeadSettings HeadSettings()
    {
        return new()
        {
            UpperLeftLocatingPin = new() { X = 0, Y = 0 },
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
    }

    public class DisplayReadMotion : DispatchProxy, IMotionDiagnostics
    {
        public Action? BeforeRead;
        public Action? BeforePositionRead;
        public Action? BeforeHome;
        public Exception? DiagnosticReadError;
        public Action<MotionAxis>? AfterDiagnosticStateRead;

        public DisplayReadMotion()
        {
            AxisMoves = [];
        }

        public IXyMotion Motion { get; set; } = null!;
        public MotionAxis? LastMovedAxis { get; private set; }
        public double? LastMoveVelocity { get; private set; }
        public double? LastMoveAccelerationSeconds { get; private set; }
        public double? LastMoveDecelerationSeconds { get; private set; }
        public List<(MotionAxis Axis, double Position)> AxisMoves { get; }

        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            if (DiagnosticReadError is { } error)
                return (null, error);
            var read = ((IMotionDiagnostics)Motion).ReadDiagnosticState(axis);
            AfterDiagnosticStateRead?.Invoke(axis);
            return read;
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            return ((IMotionDiagnostics)Motion).ReadDiagnosticPosition(axis);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name is nameof(IAxisMotion.HomeAsync) or nameof(IXyMotion.HomeHorizontalAsync))
                BeforeHome?.Invoke();
            if (method!.Name == nameof(IMotionFeedback.GetAxisState))
                BeforeRead?.Invoke();
            if (method.Name == $"get_{nameof(IMotionFeedback.Position)}")
                BeforePositionRead?.Invoke();
            if (method.Name == nameof(IAxisMotion.MoveAxisAsync))
            {
                LastMovedAxis = (MotionAxis)arguments![0]!;
                LastMoveAccelerationSeconds = (double?)arguments[4];
                LastMoveDecelerationSeconds = (double?)arguments[5];
                AxisMoves.Add(((MotionAxis)arguments![0]!, (double)arguments[1]!));
            }
            if (method.Name is nameof(IAxisMotion.MoveAxisAsync) or nameof(IXyMotion.MoveToXYAsync))
                LastMoveVelocity = (double)arguments![2]!;
            return method.Invoke(Motion, arguments);
        }
    }

    public class ScopedMotionProbe : DispatchProxy, IMotionDiagnostics
    {
        private bool _initialized;
        public IAxisMotion Motion = null!;
        public bool ReportReady;
        public bool FailHardwareCalls;
        public bool AllowStop;
        public int HardwareCalls;
        public int InitializationCalls;
        public int ResetCalls;
        public int StopCalls;
        public Action? BeforeHardwareRead;
        public Action? BeforeAxisStateRead;
        public Func<MotionAxis, AxisState, AxisState>? OverrideState;
        public Exception? DiagnosticReadError;

        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            if (DiagnosticReadError is { } error)
                return (null, error);
            if (FailHardwareCalls)
                return (null, new IOException("Unavailable diagnostic state."));
            var read = ((IMotionDiagnostics)Motion).ReadDiagnosticState(axis);
            return (read.State is { } state ? OverrideState?.Invoke(axis, state) ?? state : null, read.Error);
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            if (DiagnosticReadError is { } error)
                return (null, error);
            if (FailHardwareCalls)
                return (null, new IOException("Unavailable diagnostic position."));
            return ((IMotionDiagnostics)Motion).ReadDiagnosticPosition(axis);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            if (name == nameof(IMotionFeedback.GetAxisState))
                BeforeAxisStateRead?.Invoke();
            if (name == nameof(IAxisMotion.Stop))
                Interlocked.Increment(ref StopCalls);
            // A disabled device may reject acquisition while still accepting an explicit STOP.
            if (name == nameof(IAxisMotion.Stop) && AllowStop)
            {
                Motion.Stop();
                return null;
            }
            if (name == "get_IsReady")
            {
                BeforeHardwareRead?.Invoke();
                return ReportReady || _initialized;
            }
            if (!method.IsSpecialName
                || name is "get_IsMoving" or "get_IsMovingHorizontal")
            {
                BeforeHardwareRead?.Invoke();
                Interlocked.Increment(ref HardwareCalls);
                if (name == nameof(IAxisMotion.ResetAsync))
                    Interlocked.Increment(ref ResetCalls);
                if (FailHardwareCalls)
                    throw new IOException($"Unavailable motion: {name}");
            }

            var result = method.Invoke(Motion, arguments);
            if (name == nameof(IAxisMotion.Initialize))
            {
                _initialized = true;
                Interlocked.Increment(ref InitializationCalls);
            }
            if (name == nameof(IMotionFeedback.GetAxisState)
                && OverrideState is { } transform)
                return transform((MotionAxis)arguments![0]!, (AxisState)result!);
            if (OverrideState is { } feedback && name is "get_IsMoving" or "get_IsMovingHorizontal")
                return Motion.Axes.Any(axis => (name == "get_IsMoving" || axis != MotionAxis.Z)
                    && feedback(axis, Motion.GetAxisState(axis)).InMotion);
            return result;
        }
    }

    public enum MachineUnit
    {
        MainConveyor,
        PcbSupply,
        PcbPlacement,
        PickupBoltFeeder,
        ShootingBoltFeeder,
        BoltFastening,
        Inspection,
        NgConveyor,
    }

    public static Dictionary<OutputIo, TeachingOutputRow> TeachingRows(TeachingViewModel teaching)
    {
        return teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
            .Where(row => row.IsSupported)
            .ToDictionary(row => row.Io.Signal);
    }

    public static void PrepareCarrierTeaching(MachineSettings settings, Recipe recipe)
    {
        recipe.PcbSupply.Pcb1PickPosition.Y = 10;
        recipe.PcbSupply.Pcb2PickPosition.Y = 10;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.Pcb = new();
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Id = VirtualTestSupport.BoltId(1), Head = FasteningHead.Shooting, X = 10, Y = 10, });
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Id = VirtualTestSupport.BoltId(1, HeatSinkSlot.HeatSink2), HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 28, Y = 10 });
        foreach (var bolt in recipe.Pcb.BoltPoints)
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        TeachInspectionFovs(recipe);
    }

    public static void TeachInspectionFovs(Recipe recipe)
    {
        recipe.CarrierImages = recipe.Pcb.BoltPoints.Select((bolt, index) => new CarrierImageTile
        {
            Number = index + 1,
            Region = new(128, 88, 64, 64),
            BoltId = bolt.Id,
            HeatSink = bolt.HeatSink,
        }).ToList();
        foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
        {
            recipe.CarrierImages.Add(new()
            {
                Number = recipe.CarrierImages.Count + 1,
                Center = new() { X = pcb == HeatSinkSlot.HeatSink1 ? 10 : 28, Y = 17 },
                Region = new(180, 40, 80, 80),
                IsBarcode = true,
                HeatSink = pcb,
            });
        }
    }
}
