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
    [Fact]
    public async Task EmergencyInputStopsConveyorBeforeReadingUnrelatedMotionFeedback()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.MainConveyor) };
        settings.Units.NgCarrierTransfer = true;
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        var run = machine.StartAsync();
        var checkingTrip = new AsyncLocal<bool>();
        var readsBeforeStop = 0;
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            io.SetOutput(OutputIo.MainConveyorRun, true);
            feedback.BeforeRead = () =>
            {
                if (checkingTrip.Value && io.GetOutput(OutputIo.MainConveyorRun))
                    Interlocked.Increment(ref readsBeforeStop);
            };
            checkingTrip.Value = true;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            checkingTrip.Value = false;
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, readsBeforeStop);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
        }
        finally
        {
            checkingTrip.Value = false;
            feedback.BeforeRead = null;
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConcurrentManualAdmissionOnlyStartsOneDeviceCommand()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await feedback.Motion.MoveToXYAsync(10, 0, 10_000);
        settings.Motion.HorizontalHome.SearchSpeed = 10_000;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pauseAdmission = new AsyncLocal<bool>();
        var starts = 0;
        feedback.BeforeRead = () =>
        {
            if (pauseAdmission.Value)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        void CountStart(bool moving)
        {
            if (moving)
                Interlocked.Increment(ref starts);
        }
        feedback.Motion.MovingChanged += CountStart;

        var first = Task.Run(() =>
        {
            pauseAdmission.Value = true;
            return machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
        });
        Task second = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            second = machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
            await second.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, Volatile.Read(ref starts));
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, Volatile.Read(ref starts));
        }
        finally
        {
            release.Set();
            feedback.BeforeRead = null;
            feedback.Motion.MovingChanged -= CountStart;
            machine.Stop();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(true, MachineAlarm.Inspection)]
    [InlineData(false, MachineAlarm.NgCarrierTransfer)]
    public async Task ManualInspectionGantryIoFailureUsesTheEnabledUnitAlarm(
        bool inspectionEnabled,
        MachineAlarm expectedAlarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(inspectionEnabled ? MachineUnit.Inspection : MachineUnit.NgCarrierTransfer);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            machine.ReportManualFailure(
                machine.GetMotionAlarm(MotionGroup.InspectionGantry),
                new IOException("Manual gantry I/O failure."));
            Assert.Equal(expectedAlarm, state.Alarm);
            Assert.Equal("Manual gantry I/O failure.", state.AlarmMessage);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(ManualCommandFailure.Motion)]
    [InlineData(ManualCommandFailure.Canceled)]
    [InlineData(ManualCommandFailure.Safety)]
    [InlineData(ManualCommandFailure.Programming)]
    public async Task ManualCommandHandlesCombinedDeviceFailuresWithoutHidingProgrammingErrors(ManualCommandFailure failureKind)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        using var cancellation = new CancellationTokenSource();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Exception operationFailure = failureKind switch
        {
            ManualCommandFailure.Motion or ManualCommandFailure.Safety => new MotionException("Manual move", new IOException("Motion feedback failed.")),
            ManualCommandFailure.Canceled => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("Invalid command state."),
        };
        Exception cleanupFailure = failureKind == ManualCommandFailure.Programming
            ? new InvalidOperationException("Invalid cleanup state.")
            : new IOException("Cleanup output failed.");
        var failure = new AggregateException(operationFailure, new AggregateException(cleanupFailure));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            if (failureKind == ManualCommandFailure.Programming)
            {
                Assert.False(MachineController.IsDeviceFailure(failure));
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }
            else
            {
                Assert.True(MachineController.IsDeviceFailure(failure));
                io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
                if (failureKind == ManualCommandFailure.Canceled)
                    cancellation.Cancel();
                if (failureKind == ManualCommandFailure.Safety)
                    io.SetInput(InputIo.EmergencyStop1Pressed, true);
                machine.ReportManualFailure(machine.GetMotionAlarm(MotionGroup.InspectionGantry), failure);
                Assert.Equal(
                    failureKind switch
                    {
                        ManualCommandFailure.Safety => MachineAlarm.EmergencyStop,
                        ManualCommandFailure.Motion => MachineAlarm.MotionUnavailable,
                        _ => MachineAlarm.Inspection,
                    },
                    state.Alarm);
                Assert.Contains(operationFailure.ToString(), state.AlarmDetail);
                Assert.Contains(cleanupFailure.ToString(), state.AlarmDetail);
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            }
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualBoltTestReportsLateFailureWithoutReplacingEmergencyStop(bool emergencyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Bolt head STOP feedback failed.");
        await machine.InitializeAsync();
        try
        {
            var bus = new AdcProtocolTests.ControllerBus
            {
                StopReadFailure = failure,
                Started = () =>
                {
                    Assert.True(state.BoltTestRunning);
                    if (emergencyStop)
                        io.SetInput(InputIo.EmergencyStop1Pressed, true);
                },
            };
            using var diagnostics = new AdcProtocolViewModel(bus, settings.Hantas, machine, state);
            await diagnostics.StartCommand.ExecuteAsync(null);
            Assert.Contains("failed", diagnostics.ResultMessage);
            Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoltTestStartupFailureClearsRunningState(bool reverse)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var failure = new IOException("Feedback failed while entering bolt test.");
        void FailWhenTestingStarts()
        {
            if (!state.BoltTestRunning)
                return;
            state.Changed -= FailWhenTestingStarts;
            throw failure;
        }

        var bus = new AdcProtocolTests.ControllerBus();
        using var diagnostics = new AdcProtocolViewModel(bus, settings.Hantas, machine, state);
        state.Changed += FailWhenTestingStarts;
        try
        {
            var command = reverse ? diagnostics.ReverseCommand : diagnostics.StartCommand;
            await command.ExecuteAsync(null);

            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.Equal(0, bus.StartWrites);
        }
        finally
        {
            state.Changed -= FailWhenTestingStarts;
            state.BoltTestRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Z)]
    public async Task PlacementStopsWhenHandlerUpFeedbackIsLost(MotionAxis axis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.PcbPlacementHandler.Motion.ZSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var move = axis == MotionAxis.Z
                ? placement.MoveAxisAsync(axis, 10, stop.Token)
                : placement.JogAsync(axis, 10, stop.Token);
            await WaitUntilAsync(() => placement.Feedback.IsMoving);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(MachineAlarm.PcbPlacement, state.Alarm);
            Assert.False(placement.Feedback.IsMoving);
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.X, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.AdjustAxisAsync(MotionAxis.X, 10, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveToHorizontalZAsync());
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CylinderInterlockDuringEmergencyStopKeepsTheEmergencyAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        void TripWhileMotionHasNotFinished(bool moving)
        {
            if (!moving || !placement.Feedback.IsMovingHorizontal)
                return;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.True(placement.Feedback.IsMovingHorizontal);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        }

        placement.Feedback.MovingChanged += TripWhileMotionHasNotFinished;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                placement.MoveToXYAsync(new() { X = 20, Y = 20 }));

            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.Contains("handler lift Up", state.AlarmDetail);
            Assert.False(placement.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            placement.Feedback.MovingChanged -= TripWhileMotionHasNotFinished;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownRequiresCurrentStoppedFeedbackEvenWithoutAnOwnedMotion(bool unreadable)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        var probe = probes[MotionGroup.BoltFastening];
        if (unreadable)
            probe.FailHardwareCalls = true;
        else
            probe.OverrideState = state => state with { InMotion = true };
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        try
        {
            var failure = await Record.ExceptionAsync(machine.ShutdownAsync);

            Assert.NotNull(failure);
            Assert.Contains(nameof(MotionGroup.BoltFastening), failure.ToString());
            Assert.True(feedback.Completion.IsCompleted);
        }
        finally
        {
            probe.FailHardwareCalls = false;
            probe.OverrideState = null;
            // A retry must read the now-stopped hardware, even though acquisition has ended.
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineStartAndHomeReportAdmissionReadFailureWithoutStarting(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        if (!home)
            await machine.HomeAsync(CancellationToken.None);
        Assert.True(home ? machine.IsHomeAllowed : machine.IsStartAllowed);
        var failure = new IOException("Motion feedback failed while admitting the command.");
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            throw failure;
        };
        try
        {
            var escaped = await Record.ExceptionAsync(() => home
                ? machine.HomeAsync(CancellationToken.None)
                : machine.StartAsync());

            Assert.Null(escaped);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsRunning);
            Assert.False(feedback.Motion.IsMoving);
            Assert.True(machine.IsResetAllowed);
        }
        finally
        {
            feedback.BeforeRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeReportsReadFailureBeforeMotionStarts()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var axis = manual.Axes.Single(
            row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
        feedback.BeforeRead = () => throw new IOException("Home feedback read failed.");

        await axis.HomeCommand.ExecuteAsync(null);

        Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
        Assert.Contains("Home feedback read failed.", state.AlarmDetail);
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(services.GetRequiredService<NgCarrierTransfer>().Feedback.IsMoving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMotionStartFeedbackDoesNotBlockShutdown(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        var failure = new InvalidOperationException("Motion feedback unavailable.");
        motion.Initialize();
        motion.StateChanged += () =>
        {
            if (motion.IsMoving)
            {
                throw failure;
            }
        };

        var actual = jog
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => motion.JogAsync(MotionAxis.X, 1))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => motion.MoveAxisAsync(MotionAxis.X, 100, 1));

        Assert.Same(failure, actual);
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(motion.IsMoving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForFinalMotionFeedback(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        using var releaseFeedback = new ManualResetEventSlim();
        var feedbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        motion.Initialize();
        motion.PositionChanged += (_, _, _) =>
        {
            if (!motion.IsMoving)
            {
                feedbackEntered.TrySetResult();
                releaseFeedback.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var moving = jog ? motion.JogAsync(MotionAxis.X, 1) : motion.MoveAxisAsync(MotionAxis.X, 100, 1);

        var shutdown = Task.Run(() => operations.ShutdownAsync());
        try
        {
            await feedbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(motion.IsMoving);
            Assert.False(shutdown.IsCompleted);
            Assert.False(moving.IsCompleted);
        }
        finally
        {
            releaseFeedback.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        }
    }

    [Fact]
    public async Task ManualJogFaultStopsTheMachineAndAllowsReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                io.SetOutput(OutputIo.NgConveyorRun, true);
                throw fault;
            }
        };
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
        using var stopped = new CancellationTokenSource();
        var jog = gantry.JogAsync(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Feedback.IsMoving);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task HomeAdmissionAllowsPreparationButAxisMotionRequiresRaisedCylinders()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.IsHomeAllowed);
        Assert.False(state.ManualControlsEnabled);
        Assert.True(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, true);
        using (services.GetRequiredService<OperationCancellation>().Link())
            Assert.False(state.ManualSetupEnabled);
        Assert.True(state.ManualSetupEnabled);
        var outputsChanged = 0;
        io.OutputChanged += (_, _) => outputsChanged++;

        foreach (var (up, down, reason) in new[]
        {
            (
                InputIo.PcbPlacementHandlerUp,
                InputIo.PcbPlacementHandlerDown,
                HomeBlockReason.PlacementNotRaised),
            (InputIo.PcbPlacementIpmUp, InputIo.PcbPlacementIpmDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.PickupHeadUp, InputIo.PickupHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.ShootingHeadUp, InputIo.ShootingHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.NgCarrierPickupUp, InputIo.NgCarrierPickupDown, HomeBlockReason.NgPickupNotRaised),
        })
        {
            io.SetInput(up, false);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            Assert.True(machine.IsHomeAllowed);
            io.SetInput(down, true);
            io.SetInput(up, true);
            Assert.Equal(reason, machine.GetHomeBlock(requireRaised: true));
            io.SetInput(down, false);
            Assert.True(machine.IsHomeAllowed);
        }

        Assert.Equal(0, outputsChanged);

        settings.Units.PcbSupply = settings.Units.PcbPlacement = settings.Units.BoltFastening = false;
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.True(machine.IsHomeAllowed);
        services.GetRequiredService<MachineState>().Refresh();
        await WaitUntilAsync(() => manual.Axes[9].HomeCommand.CanExecute(null));
        Assert.False(manual.Axes[3].HomeCommand.CanExecute(null));
        Assert.True(manual.Axes[9].HomeCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeCanRepeatAndIgnoresCarrierInputs(bool individualAxis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
            Assert.True(machine.IsHomeAllowed);
            var axis = manual.Axes.Single(
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            await WaitUntilAsync(() => axis.HomeCommand.CanExecute(null));
            var moved = false;
            gantry.Feedback.MovingChanged += moving =>
            {
                if (moving)
                {
                    moved = true;
                    io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                    io.SetInput(InputIo.NgCarrierDetected, true);
                }
            };

            for (var run = 0; run < 2; run++)
            {
                moved = false;
                Assert.True(machine.IsHomeAllowed);
                if (individualAxis)
                    await axis.HomeCommand.ExecuteAsync(null);
                else
                    await machine.HomeAsync(CancellationToken.None);

                Assert.True(moved);
                Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
                Assert.True(io.GetInput(InputIo.NgCarrierDetected));
                Assert.True(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.False(state.IsHoming);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task BoltHomeIgnoresStationaryNgPickupState()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.NgCarrierTransfer = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        try
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
            io.SetInput(InputIo.NgCarrierDetected, true);
            var ngMoved = false;
            gantry.Feedback.MovingChanged += moving => ngMoved |= moving;
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));

            await teaching.HomeCommand.ExecuteAsync(null);

            var fastening = services.GetRequiredService<BoltFasteningStation>();
            Assert.All(fastening.Feedback.Axes, axis => Assert.True(fastening.Feedback.GetAxisState(axis).Homed));
            Assert.False(ngMoved);
            Assert.True(io.GetInput(InputIo.NgCarrierPickupDown));
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsHoming);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomeRaisesCylindersBeforeMovingAxesAndDisplaysHoming()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        IIoService signals = io;
        await machine.InitializeAsync();
        OutputIo[] cylinders = [
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PcbPlacementIpmDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown,
            OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(
            output => signals.SetOutputAndWaitAsync(output, true)));
        var motions = new[]
        {
            services.GetRequiredService<PcbSupplier>().Feedback,
            services.GetRequiredService<PcbPlacer>().Feedback,
            services.GetRequiredService<BoltFasteningStation>().Feedback,
            services.GetRequiredService<NgCarrierTransfer>().Feedback,
        };
        var moved = false;
        var movedBeforeRaised = false;
        foreach (var motion in motions)
            motion.MovingChanged += moving =>
            {
                moved |= moving;
                movedBeforeRaised |= moving && machine.GetHomeBlock(requireRaised: true) != HomeBlockReason.None;
            };
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        await WaitUntilAsync(() => !machine.IsHomeAllowed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        Assert.True(machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsHomeAllowed);

        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.IsHoming);
        Assert.True(state.IsRunning);
        Assert.True(state.IsHoming);
        Assert.Equal(MachineDisplayState.Homing, services.GetRequiredService<OperationViewModel>().MachineDisplayState);
        Assert.False(state.ManualSetupEnabled);
        Assert.False(machine.IsHomeAllowed);
        await homing;
        await WaitUntilAsync(() => machine.IsHomeAllowed && state.Homed);
        Assert.True(machine.IsHomeAllowed);
        Assert.True(state.Homed);
        Assert.False(state.IsHoming);
        Assert.True(moved);
        Assert.False(movedBeforeRaised);
        Assert.Equal(cylinders.Order(), outputChanges.Order());
        Assert.All(cylinders, output => Assert.False(io.GetOutput(output)));
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmUp));
        Assert.False(io.GetInput(InputIo.PcbPlacementIpmDown));
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsHomeAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRaisesCylindersBeforeAutomaticAndPreservesHeldPcb(bool holdingPcb)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Options.UseDoorInterlock = false;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await placement.SetLiftDownAsync(true);
        await placement.SetIpmLiftDownAsync(true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, holdingPcb);
        io.SetInput(InputIo.AutoMode, false);
        var started = false;
        var readyBeforeStarting = false;
        void StopAtAutomaticStart()
        {
            if (!state.AutomaticRunning || started)
                return;
            started = true;
            readyBeforeStarting = placement.HandlerRaised
                && placement.IpmLift == (holdingPcb ? PlacementCylinderState.Down : PlacementCylinderState.Up);
            machine.Stop();
        }

        state.Changed += StopAtAutomaticStart;
        try
        {
            Assert.True(machine.IsStartAllowed);
            await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(started);
            Assert.True(readyBeforeStarting);
            Assert.Equal(holdingPcb, io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
        finally
        {
            state.Changed -= StopAtAutomaticStart;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomePreparationStopsBeforeLiftingIpmWhenPlacementDetectsPcb()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        IIoService signals = io;
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        void DetectPcb(OutputIo output, bool on)
        {
            if (output == OutputIo.PcbPlacementHandlerDown && !on)
                io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        }

        io.OutputChanged += DetectPcb;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);
            Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
            Assert.False(machine.IsHomeAllowed);
        }
        finally
        {
            io.OutputChanged -= DetectPcb;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeAndStartPreparationStopBeforeAxesOnStopOrTimeout(bool start)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        settings.Options.TimeoutMilliseconds = 500;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        if (start)
            await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        var operation = services.GetRequiredService<OperationViewModel>();
        var command = start ? operation.StartCommand : operation.HomeCommand;
        var running = command.ExecuteAsync(null);
        await WaitUntilAsync(() => state.IsRunning && !io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(!start, state.IsHoming);
        Assert.False(state.AutomaticRunning);
        Assert.True(operation.StopCommand.CanExecute(null));
        await operation.StopCommand.ExecuteAsync(null);
        await running;
        Assert.False(state.IsHoming);
        Assert.Equal(start, state.Homed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementHandlerDown));
        Assert.True(machine.IsHomeAllowed);

        await command.ExecuteAsync(null);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(state.IsRunning);
        Assert.Equal(start, state.Homed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomePreparationFailureAfterStopIsReportedWithoutReplacingSafetyAlarm(bool safetyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgCarrierTransfer) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Cylinder output failed after STOP.");
        await machine.InitializeAsync();
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        void FailAfterStop(OutputIo output, bool on)
        {
            if (output != OutputIo.NgCarrierPickupDown || on)
                return;
            io.OutputChanged -= FailAfterStop;
            if (safetyStop)
                io.SetInput(InputIo.AirPressureHigh, false);
            else
                machine.Stop();
            throw failure;
        }

        io.OutputChanged += FailAfterStop;
        try
        {
            Assert.True(machine.IsHomeAllowed);
            await machine.HomeAsync(CancellationToken.None);

            Assert.Equal(
                safetyStop ? MachineAlarm.AirPressureLow : MachineAlarm.NgCarrierTransfer,
                state.Alarm);
            if (safetyStop)
                Assert.Null(state.AlarmDetail);
            else
                Assert.Equal(failure.ToString(), state.AlarmDetail);
            var entry = Assert.Single(
                services.GetRequiredService<ApplicationLog>().Snapshot(),
                entry => entry.Detail == failure.ToString());
            Assert.Contains(nameof(MachineAlarm.NgCarrierTransfer), entry.Message);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.False(state.IsRunning);
        }
        finally
        {
            io.OutputChanged -= FailAfterStop;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionHomeRaisesPickupAndIgnoresCarrierInput()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<NgCarrierTransfer>();
        IIoService signals = io;
        await machine.InitializeAsync();
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, true);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.True(machine.IsHomeAllowed);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.HomeAxisAsync(MotionAxis.X));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.Homed);

        var transfer = services.GetRequiredService<NgCarrierTransfer>();
        Assert.False(transfer.IsRaised);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));

        var unsafeMovement = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && state.IsHoming)
            {
                unsafeMovement |= !transfer.IsRaised;
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.False(unsafeMovement);
        Assert.True(transfer.IsRaised);
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.JogAsync(MotionAxis.X, 10));
        await Assert.ThrowsAsync<MotionInterlockException>(
            async () => await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        var moving = gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 10);
        await WaitUntilAsync(() => gantry.Feedback.IsMoving);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.Contains("pickup Up", state.AlarmDetail);
        Assert.Contains("Current lift: Between", state.AlarmDetail);

        io.SetInput(InputIo.NgCarrierPickupUp, true);
        await machine.ResetAsync();
        await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 1000);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task MotionRequiresRaisedCylindersWhileFasteningZCanRetract(
        MotionGroup group,
        InputIo up,
        InputIo down)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 10;
        settings.BoltFastening.Motion.HorizontalSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        var isPlacement = group == MotionGroup.PcbPlacementHandler;
        var feedback = isPlacement ? placement.Feedback : fastening.Feedback;
        Task MoveXY()
        {
            return isPlacement
                ? placement.MoveToXYAsync(new() { X = 20, Y = 20 })
                : fastening.MoveToXYAsync(20, 20);
        }

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 1));
        else
            await fastening.MoveZAsync(1);
        Assert.Equal(isPlacement ? 0 : 1, feedback.GetPosition().Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => isPlacement
                ? placement.HomeAxisAsync(MotionAxis.X)
                : fastening.HomeAxisAsync(MotionAxis.X));
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.Y, 10));

        io.SetInput(up, true);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        io.SetInput(down, false);
        var move = MoveXY();
        await WaitUntilAsync(() => feedback.IsMovingHorizontal);
        io.SetInput(up, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.False(feedback.IsMoving);
        Assert.False(feedback.IsMovingHorizontal);
        Assert.Equal(isPlacement ? MachineAlarm.PcbPlacement : MachineAlarm.BoltFastening, state.Alarm);
        Assert.Contains(isPlacement ? "axis movement" : "horizontal movement", state.AlarmDetail);
        Assert.Contains("Between", state.AlarmDetail);
    }

    [Fact]
    public async Task PlacementHoldingPcbBlocksIpmRaiseForHomeButNotHorizontalTravel()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        Assert.Equal(HomeBlockReason.PlacementHoldingPcb, machine.HomeBlock);
        Assert.True(placement.HandlerRaised);
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));

        var move = placement.MoveToXYAsync(new() { X = 20, Y = 20 });
        await WaitUntilAsync(() => placement.Feedback.IsMovingHorizontal);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        await move;

        Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
        Assert.Equal(
            (20, 20, settings.PcbPlacementHandler.HandoffPosition.Z),
            placement.Feedback.GetPosition());
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task ManualBoltPickupAndReturnPreserveOrderAndVacuumOnStopAndRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        settings.BoltFastening.PickupPosition = new() { X = 40, Y = 30, Z = 12 };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(14);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        Assert.Equal(TeachingSaveBehavior.BoltPickup, teaching.SaveBehavior);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));

        // Hold only the head feedback below; the table is already at its pickup position.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        var vacuumChanged = false;
        var movedXyWithHeadDown = false;
        var loweredAt = new ConcurrentQueue<(double X, double Y, double Z)>();
        io.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump)
                vacuumChanged = true;
            if (output == OutputIo.PickupHeadDown && value)
                loweredAt.Enqueue(gantry.Feedback.GetPosition());
        };
        gantry.Feedback.PositionChanged += (_, _, _) =>
            movedXyWithHeadDown |= gantry.Feedback.IsMovingHorizontal
                && !gantry.IsHorizontalMoveAllowed;

        var move = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(move.IsCompleted);
        Assert.False(io.GetInput(InputIo.PickupHeadDown));
        teaching.JogStopCommand.Execute(null);
        await move.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Stop keeps pneumatic outputs.

        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        var retry = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.False(retry.IsCompleted);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.PickupHeadDown, true);
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 12), gantry.Feedback.GetPosition());
        Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
        Assert.Equal(BoltCylinderState.Up, gantry.ShootingHeadPosition);
        await WaitUntilAsync(() => teaching.ReturnFromPickupCommand.CanExecute(null));
        var stopAtSafeZ = true;
        gantry.Feedback.PositionChanged += (_, _, z) =>
        {
            if (stopAtSafeZ
                && Math.Abs(z - settings.BoltFastening.SafeZ) <= MotionService.PositionToleranceMillimeters)
            {
                stopAtSafeZ = false;
                teaching.JogStopCommand.Execute(null);
            }
        };
        await teaching.ReturnFromPickupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopAtSafeZ);
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Cancellation must not advance to Head Up.
        Assert.False(gantry.Feedback.IsMoving);

        var returning = teaching.ReturnFromPickupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(returning.IsCompleted); // Down DO turning OFF alone is not completion.
        io.SetInput(InputIo.PickupHeadDown, false);
        io.SetInput(InputIo.PickupHeadUp, true);
        await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gantry.IsHorizontalMoveAllowed);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(vacuumChanged);
        Assert.False(movedXyWithHeadDown);
        Assert.Equal(2, loweredAt.Count);
        Assert.All(loweredAt, position => Assert.Equal((40, 30, 5), position));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupAndReturnReportCylinderTimeout(bool returning)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        if (returning)
            await gantry.MoveToPickupPositionAsync();
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Feedback.GetPosition().Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FasteningAdjustmentStopsOnModeOrServoLoss(bool autoMode)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var jog = motion.JogAsync(MotionAxis.X, 1);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 0);
        if (autoMode)
            io.SetInput(InputIo.AutoMode, false);
        else
            gantry.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Feedback.GetPosition();
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
        Assert.Equal(stopped, gantry.Feedback.GetPosition());
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
    }

    [Fact]
    public async Task CylinderFeedbackIsRecheckedBetweenTravelZAndXy()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        var before = gantry.Feedback.GetPosition();
        io.AutoResponseEnabled = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && !gantry.Feedback.IsMovingHorizontal)
                io.SetInput(InputIo.PickupHeadUp, false);
        };

        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.MoveToXYAsync(20, 20));

        var after = gantry.Feedback.GetPosition();
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Feedback.IsMoving);
    }

    [Theory]
    [InlineData(MachineUnit.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry)]
    [InlineData(MachineUnit.NgCarrierTransfer, MotionGroup.InspectionGantry)]
    public async Task OnlyEnabledMotionsAreInitializedReadResetAndHomed(
        MachineUnit unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        foreach (var (candidate, probe) in probes)
        {
            if (candidate == group)
                continue;
            // Also simulate an initialized drive that was subsequently disabled.
            probe.ReportReady = true;
            probe.FailHardwareCalls = true;
        }

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Available);
        Assert.False(state.Faulted);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Ready);
        await WaitUntilAsync(() => state.ManualControlsEnabled);

        probes[group].Motion.SetServo(MotionAxis.X, false);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        Assert.True(state.Ready);
        Assert.Equal(1, probes[group].ResetCalls);

        if (group == MotionGroup.BoltFastening)
        {
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            teaching.StepDistance = 0.1;
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            var before = probes[group].Motion.GetPosition();
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + 0.1, probes[group].Motion.GetPosition().X, precision: 6);
        }

        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler)
        {
            var placer = services.GetRequiredService<PcbPlacer>();
            Assert.NotEqual(PcbPlacementState.ReceivingPcb,
                placer.State);
        }

        foreach (var row in manual.Axes.Where(row => row.Group != group))
        {
            Assert.False(row.ToggleServoCommand.CanExecute(null));
            Assert.False(row.HomeCommand.CanExecute(null));
            Assert.Null(row.Diagnostics.Snapshot.State);
            // Bypassing CanExecute still must not command a disabled drive.
            row.ToggleServoCommand.Execute(null);
            await row.HomeCommand.ExecuteAsync(null);
        }

        Assert.All(
            probes.Where(item => item.Key != group),
            item => Assert.Equal(0, item.Value.HardwareCalls));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task DisablingAFailedMotionAllowsResetWithoutHidingAnEnabledMotionFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        probes[MotionGroup.PcbSupply].FailHardwareCalls = true;

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.ManualControlsEnabled);
        var failedCalls = probes[MotionGroup.PcbSupply].HardwareCalls;

        settings.Units.PcbSupply = false;
        settings.Units.PcbPlacement = true;
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        await machine.ResetAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Ready);
        Assert.True(state.ManualControlsEnabled);
        Assert.Equal(failedCalls, probes[MotionGroup.PcbSupply].HardwareCalls);
        // Re-enabling the same faulty hardware makes it mandatory again.
        settings.Units.PcbSupply = true;
        Assert.True(state.Faulted);
        Assert.False(state.ManualControlsEnabled);
        var placementResets = probes[MotionGroup.PcbPlacementHandler].ResetCalls;
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Equal(placementResets + 1, probes[MotionGroup.PcbPlacementHandler].ResetCalls);
    }

    [Theory]
    [InlineData(InputIo.PickupHeadUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, false)]
    [InlineData(InputIo.PcbPlacementIpmUp, false, true)]
    public async Task HomeStopsWhenItsCylinderConditionChanges(
        InputIo input,
        bool value,
        bool teachingHome)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await Task.WhenAll(placement.MoveAxisAsync(MotionAxis.Z, 50), fastening.MoveZAsync(50));
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbPlacementHandler;
        await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => placement.Feedback.IsMoving && (teachingHome || fastening.Feedback.IsMoving));
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(input, value);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsHoming);
        Assert.False(placement.Feedback.IsMoving);
        Assert.False(fastening.Feedback.IsMoving);
        Assert.False(placement.Feedback.GetAxisState(MotionAxis.Z).Homed);
        Assert.NotEqual(HomeBlockReason.None, machine.GetHomeBlock(requireRaised: true));
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        if (teachingHome)
        {
            await WaitUntilAsync(() => teaching.HomeCommand.CanExecute(null));
        }
    }

    [Fact]
    public async Task MotionAlarmBlocksHomeAndStopsAllHomingAxes()
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 20;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbPlacementHandler);
        var fastening = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        Assert.False(machine.IsHomeAllowed);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        Assert.True(machine.IsHomeAllowed);

        await Task.WhenAll(
            placement.MoveAxisAsync(MotionAxis.Z, 50, 10_000),
            fastening.MoveAxisAsync(MotionAxis.Z, 50, 10_000));
        // The command can finish before the motion scan publishes stopped feedback.
        await WaitUntilAsync(() => machine.IsHomeAllowed);
        var homing = machine.HomeAsync(CancellationToken.None);
        Assert.True(await VirtualTest.WaitUntilAsync(
            () => placement.IsMoving && fastening.IsMoving, TimeSpan.FromSeconds(2)),
            $"Home completed={homing.IsCompleted}, IsHomeAllowed={machine.IsHomeAllowed}, "
                + $"block={machine.HomeBlock}, alarm={state.Alarm}, detail={state.AlarmDetail}");
        fastening.SetAlarm(MotionAxis.X, true);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsHoming);
        Assert.False(state.IsRunning);
        Assert.False(placement.IsMoving);
        Assert.False(fastening.IsMoving);
        Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(fastening.GetAxisState(MotionAxis.Z).Homed);
    }

    [Fact]
    public async Task ParallelHomeReportsEachUnitsFailureAfterTheFirstFailureStopsHome()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.BoltFastening = true;
        var results = new Dictionary<MotionGroup, HomeResultMotion>();
        IXyMotion Wrap(IServiceProvider provider, MotionGroup group)
        {
            var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
            var result = (HomeResultMotion)motion;
            result.Motion = provider.GetRequiredKeyedService<IXyMotion>(group);
            result.AwaitCleanupAfterCancellation = true;
            results.Add(group, result);
            return motion;
        }

        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton(provider => new PcbPlacer(Wrap(provider, MotionGroup.PcbPlacementHandler),
                        provider.GetRequiredService<IIoService>(),
                        settings.PcbPlacementHandler,
                        provider.GetRequiredService<IPcbSupplyHandoff>(),
                        provider.GetRequiredService<PcbPlacementWork>(),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>()))
            .AddSingleton(provider => new BoltFasteningStation(provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                        provider.GetRequiredService<IIoService>(),
                        Wrap(provider, MotionGroup.BoltFastening),
                        settings.BoltFastening,
                        settings.CarrierReference,
                        provider.GetRequiredService<BoltFasteningWork>(),
                        provider.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Pickup),
                        provider.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Shooting),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>()))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        var homing = machine.HomeAsync(default);
        var firstFailure = new MotionException("Placement home", new IOException("Placement home failed."));
        var stopFailure = new MotionException("Fastening STOP", new IOException("Fastening stop failed."));
        try
        {
            await Task.WhenAll(results.Values.Select(result => result.Started.Task))
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.IsHoming);
            results[MotionGroup.PcbPlacementHandler].Result.SetException(firstFailure);
            await WaitUntilAsync(() => results[MotionGroup.BoltFastening].HomeCancellation.IsCancellationRequested);
            Assert.False(homing.IsCompleted);
            results[MotionGroup.BoltFastening].Result.SetException(stopFailure);
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Contains("Placement home failed.", state.AlarmDetail);
            Assert.Contains(log.Snapshot(), entry => entry.Detail?.Contains("Fastening stop failed.") == true);
            Assert.False(state.IsHoming);
            Assert.All(results.Values, result => Assert.Equal(0, result.HorizontalHomeCalls));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            foreach (var result in results.Values)
                result.Result.TrySetCanceled();
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(
        bool exception,
        bool individual,
        bool teachingHome,
        bool safetyStop)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings))
            motionSettings.ZHome.SearchSpeed = 1;
        HomeResultMotion? homeResult = null;
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .AddSingleton(
                provider =>
                {
                    var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                    homeResult = (HomeResultMotion)motion;
                    homeResult.AwaitCleanupAfterCancellation = safetyStop;
                    homeResult.Motion = provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
                    return new BoltFasteningStation(provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                        provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                        provider.GetRequiredService<IIoService>(),
                        motion,
                        settings.BoltFastening,
                        settings.CarrierReference,
                        provider.GetRequiredService<BoltFasteningWork>(),
                        provider.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Pickup),
                        provider.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Shooting),
                        provider.GetRequiredService<RecipeManager>(),
                        provider.GetRequiredService<UnitSettings>());
                })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        await placement.MoveAxisAsync(MotionAxis.Z, 50, 10_000);

        var axisRow = manual.Axes.Single(
            row => row.Group == MotionGroup.BoltFastening && row.Axis == MotionAxis.Z);
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var homing = teachingHome
            ? teaching.HomeCommand.ExecuteAsync(null)
            : individual
                ? axisRow.HomeCommand.ExecuteAsync(null)
                : machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(
                () => individual || teachingHome ? state.IsHoming : placement.IsMoving && supply.IsMoving);
            if (safetyStop)
            {
                services.GetRequiredService<VirtualIoService>().SetInput(InputIo.EmergencyStop1Pressed, true);
            }
            if (exception)
            {
                homeResult!.Result.SetException(
                    new MotionException("Home", new InvalidOperationException("Home command failed.")));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }

            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            var expectedAlarm = safetyStop ? MachineAlarm.EmergencyStop : MachineAlarm.HomeFailed;
            Assert.Equal(expectedAlarm, state.Alarm);
            if (exception)
                Assert.Contains("Home command failed.", state.AlarmDetail);
            await WaitUntilAsync(() => state.Alarm == expectedAlarm && !state.IsRunning);
            // A latched home failure does not block a retry while the axis feedback remains healthy.
            Assert.Equal(!safetyStop, axisRow.HomeCommand.CanExecute(null));
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            axisRow.HomeCommand.Cancel();
            teaching.HomeCommand.Cancel();
            machine.Stop();
            await homing;
        }
    }

    public enum ManualCommandFailure
    {
        Motion,
        Canceled,
        Safety,
        Programming,
    }
}
