using IBTM.BoltFeeder;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static ServiceProvider CreateDisplayServices(
        out DisplayReadMotion feedback,
        MachineSettings? settings = null)
    {
        var motion = DispatchProxy.Create<IXyMotion, DisplayReadMotion>();
        var probe = (DisplayReadMotion)motion;
        feedback = probe;
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings ?? FlowSettings())
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
            {
                var motions = Enum.GetValues<MotionGroup>().ToDictionary(
                    group => group, group => provider.GetRequiredKeyedService<IXyMotion>(group));
                probe.Motion = motions[MotionGroup.InspectionGantry];
                motions[MotionGroup.InspectionGantry] = motion;
                return motions;
            })
            .BuildServiceProvider();
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
    {
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true, });
    }

    private static Dictionary<OutputIo, TeachingOutputRow> TeachingRows(TeachingViewModel teaching)
    {
        return teaching.TeachingIoGroups.SelectMany(group => group.Outputs)
            .Where(row => row.IsSupported)
            .ToDictionary(row => row.Io.Signal);
    }

    private static ServiceProvider CreateMotionScopeServices(
        MachineSettings settings,
        out Dictionary<MotionGroup, ScopedMotionProbe> probes,
        Action<IServiceCollection>? configure = null)
    {
        var captured = new Dictionary<MotionGroup, ScopedMotionProbe>();
        probes = captured;
        T Wrap<T>(MotionGroup group, T motion)
            where T : class, IAxisMotion
        {
            var wrapper = DispatchProxy.Create<T, ScopedMotionProbe>();
            var probe = (ScopedMotionProbe)(object)wrapper;
            probe.Motion = motion;
            captured.Add(group, probe);
            return wrapper;
        }

        var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton<IReadOnlyDictionary<MotionGroup, IXyMotion>>(provider =>
                Enum.GetValues<MotionGroup>().ToDictionary(group => group,
                    group => Wrap(group, provider.GetRequiredKeyedService<IXyMotion>(group))));
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        // These tests replace the handler factories that normally initialize virtual feedback.
        _ = provider.GetRequiredService<VirtualMachine>();
        return provider;
    }

    private static UnitSettings EnableOnly(MachineUnit unit)
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

    private static HomeSettings FastHome()
    {
        return new() { SearchSpeed = 10_000 };
    }

    private static MotionSettings[] MotionSettingsOf(MachineSettings settings)
    {
        return [
            settings.PcbSupply.Motion,
            settings.PcbPlacementHandler.Motion,
            settings.BoltFastening.Motion,
            settings.InspectionGantry.Motion,
        ];
    }

    private static void FastHomes(MachineSettings settings)
    {
        foreach (var motion in MotionSettingsOf(settings))
        {
            motion.HorizontalHome = FastHome();
            motion.ZHome = FastHome();
        }
    }

    private static MachineSettings FlowSettings()
    {
        var settings = new MachineSettings();
        FastHomes(settings);
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.RotationZ = 0;
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

    private static MotionSettings FastMotion()
    {
        return new()
        {
            HorizontalSpeed = 10_000,
            ZSpeed = 10_000,
            HorizontalHome = FastHome(),
            ZHome = FastHome(),
        };
    }

    private static BoltHeadSettings HeadSettings()
    {
        return new()
        {
            UpperLeftLocatingPin = new() { X = 0, Y = 0 },
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
    }

    private static void PrepareCarrierTeaching(MachineSettings settings, Recipe recipe)
    {
        recipe.PcbSupply.Pcb1PickPosition.Y = 10;
        recipe.PcbSupply.Pcb2PickPosition.Y = 10;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.Pcb = new();
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10, });
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 28, Y = 10 });
        foreach (var bolt in recipe.Pcb.BoltPoints)
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        TeachInspectionFovs(settings, recipe);
    }

    private static void TeachInspectionFovs(MachineSettings settings, Recipe recipe)
    {
        recipe.CarrierImages = recipe.Pcb.BoltPoints.Select((bolt, index) => new CarrierImageTile
        {
            Number = index + 1,
            Region = new(128, 88, 64, 64),
            BoltNumber = bolt.Number,
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

    public class HomeResultMotion : DispatchProxy
    {
        public HomeResultMotion()
        {
            Result = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; }
        public int HorizontalHomeCalls { get; private set; }
        public bool AwaitCleanupAfterCancellation { get; set; }
        public Exception? StartFailure { get; set; }
        public TaskCompletionSource Started { get; }
        public CancellationToken HomeCancellation { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                HomeCancellation = (CancellationToken)arguments[2]!;
                Started.TrySetResult();
                if (StartFailure is not null)
                    throw StartFailure;
                return AwaitCleanupAfterCancellation
                    ? Result.Task
                    : Result.Task.WaitAsync(HomeCancellation);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
    }

    private sealed class StoppingBoltHead : IBoltHead
    {
        public StoppingBoltHead()
        {
            Started = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopping = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopped = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource Started { get; }
        public TaskCompletionSource Stopping { get; }
        public TaskCompletionSource Stopped { get; }


        public AdcStatusMonitor? Monitor => null;

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }



        public async Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0)
        {
            Started.SetResult();
            try
            {
                if (feedAsync is not null)
                    await feedAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new(true, 1);
            }
            finally
            {
                Stopping.SetResult();
                await Stopped.Task;
            }
        }
    }

    private sealed class WaitingBoltHead : IBoltHead
    {
        public WaitingBoltHead()
        {
            ReadinessEntered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ReadinessReleased = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; }
        public TaskCompletionSource ReadinessReleased { get; }


        public AdcStatusMonitor? Monitor => null;

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            ReadinessChecks++;
            if (!WaitForReadiness)
            {
                return Task.CompletedTask;
            }

            ReadinessEntered.TrySetResult();
            return ReadinessReleased.Task.WaitAsync(cancellationToken);
        }

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return CheckReadyAsync(cancellationToken);
        }

        public Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0)
        {
            throw new NotSupportedException();
        }


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
        public Action? BeforeHardwareRead;
        public Func<AxisState, AxisState>? OverrideState;
        public Exception? DiagnosticReadError;

        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            switch (true)
            {
                case true when DiagnosticReadError is { } error:
                    return (null, error);
                case true when FailHardwareCalls:
                    return (null, new IOException("Unavailable diagnostic state."));
            }
            var read = ((IMotionDiagnostics)Motion).ReadDiagnosticState(axis);
            return (read.State is { } state ? OverrideState?.Invoke(state) ?? state : null, read.Error);
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            switch (true)
            {
                case true when DiagnosticReadError is { } error:
                    return (null, error);
                case true when FailHardwareCalls:
                    return (null, new IOException("Unavailable diagnostic position."));
                default:
                    return ((IMotionDiagnostics)Motion).ReadDiagnosticPosition(axis);
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            // A disabled device may reject acquisition while still accepting an explicit STOP.
            switch (true)
            {
                case true when name == nameof(IAxisMotion.Stop) && AllowStop:
                    Motion.Stop();
                    return null;
                case true when name == "get_IsReady":
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
                return transform((AxisState)result!);
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
}
