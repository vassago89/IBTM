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
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
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
    public class HomeResultMotion : DispatchProxy
    {
        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int HorizontalHomeCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                return Result.Task.WaitAsync((CancellationToken)arguments[2]!);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StoppingBoltHead : IBoltHead
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BoltHeadState State
        {
            get
            {
                return BoltHeadState.Ready;
            }
        }

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

        public void DiscardPendingResult()
        {
        }

        public async Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
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
        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadinessReleased { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public BoltHeadState State
        {
            get
            {
                return BoltHeadState.Ready;
            }
        }

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

        public Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void DiscardPendingResult()
        {
        }
    }

    private static ServiceProvider CreateDisplayServices(out DisplayReadMotion feedback)
    {
        var motion = DispatchProxy.Create<IXyMotion, DisplayReadMotion>();
        var probe = (DisplayReadMotion)motion;
        feedback = probe;
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(FlowSettings(), new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(
                provider =>
                {
                    probe.Motion = provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
                    return new InspectionGantry(
                        motion,
                        provider.GetRequiredService<NgCarrierTransfer>(),
                        provider.GetRequiredService<OperationCancellation>(),
                        provider.GetRequiredService<InspectionGantrySettings>());
                })
            .BuildServiceProvider();
    }

    public class DisplayReadMotion : DispatchProxy
    {
        public IXyMotion Motion { get; set; } = null!;

        public Action? BeforeRead;
        public Action? BeforePositionRead;
        public MotionAxis? LastMovedAxis { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IMotionFeedback.GetAxisState))
                BeforeRead?.Invoke();
            if (method.Name == nameof(IMotionFeedback.GetPosition))
                BeforePositionRead?.Invoke();
            if (method.Name == nameof(IAxisMotion.MoveAxisAsync))
                LastMovedAxis = (MotionAxis)arguments![0]!;
            return method.Invoke(Motion, arguments);
        }
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
    {
        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true, });
    }

    private static ServiceProvider CreateMotionScopeServices(
        MachineSettings settings,
        out Dictionary<MotionGroup, ScopedMotionProbe> probes)
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

        return new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(
                provider =>
                    new PcbSupplyHandler(
                        Wrap(
                            MotionGroup.PcbSupply,
                            provider.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply)),
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbSupply,
                        settings.PcbBuffer))
            .AddSingleton(
                provider =>
                    new PcbPlacementHandler(
                        Wrap(
                            MotionGroup.PcbPlacementHandler,
                            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler)),
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbPlacementHandler))
            .AddSingleton(
                provider =>
                    new BoltFasteningGantry(
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                        provider.GetRequiredService<IIoService>(),
                        Wrap(
                            MotionGroup.BoltFastening,
                            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening)),
                        settings.BoltFastening,
                        settings.CarrierReference))
            .AddSingleton(
                provider =>
                    new InspectionGantry(
                        Wrap(
                            MotionGroup.InspectionGantry,
                            provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry)),
                        provider.GetRequiredService<NgCarrierTransfer>(),
                        provider.GetRequiredService<OperationCancellation>(),
                        settings.InspectionGantry))
            .BuildServiceProvider();
    }

    public class ScopedMotionProbe : DispatchProxy
    {
        private bool _initialized;
        public IAxisMotion Motion = null!;
        public bool ReportReady;
        public bool FailHardwareCalls;
        public int HardwareCalls;
        public int ResetCalls;
        public Func<AxisState, AxisState>? OverrideState;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            if (name == "get_IsReady")
                return ReportReady || _initialized;
            if ((!method.IsSpecialName && name != nameof(IMotionFeedback.GetRange))
                || name == "get_IsAtHorizontalZ")
            {
                Interlocked.Increment(ref HardwareCalls);
                if (name == nameof(IAxisMotion.Reset))
                    Interlocked.Increment(ref ResetCalls);
                if (FailHardwareCalls)
                    throw new IOException($"Unavailable motion: {name}");
            }

            var result = method.Invoke(Motion, arguments);
            if (name == nameof(IAxisMotion.Initialize))
                _initialized = true;
            if (name == nameof(IMotionFeedback.GetAxisState)
                && OverrideState is { } transform)
                return transform((AxisState)result!);
            return result;
        }
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
            NgCarrierTransfer = unit == MachineUnit.NgCarrierTransfer,
            NgShuttle = unit == MachineUnit.NgShuttle,
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
        var settings = new MachineSettings
        {
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
        };
        FastHomes(settings);
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.RotationZ = 0;
        settings.PcbSupply.CarrierY = 10;
        settings.PcbSupply.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.PcbSupply.BufferClearZ = 20;
        settings.PcbBuffer.SupplyBoundary1 = 60;
        settings.PcbBuffer.SupplyBoundary2 = 100;
        settings.PcbBuffer.PlacementBoundary1 = new() { X = 60, Y = 20 };
        settings.PcbBuffer.PlacementBoundary2 = new() { X = 100, Y = 40 };
        settings.PcbPlacementHandler.Motion = FastMotion();
        settings.PcbPlacementHandler.BufferEntryZ = 0;
        settings.PcbPlacementHandler.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.BoltFastening.Motion = FastMotion();
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
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgCarrierTransfer.Speed = 10_000;
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
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
            LowerRightLocatingPin = new() { X = 100, Y = 0 },
        };
    }

    private static void PrepareCarrierTeaching(MachineSettings settings, Recipe recipe)
    {
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.Pcb = VirtualTest.TaughtPcbLayout();
        recipe.Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10, });
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
        NgCarrierTransfer,
        NgShuttle,
        NgConveyor,
    }

}
