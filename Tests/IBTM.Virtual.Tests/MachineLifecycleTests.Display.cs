using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public async Task SupplyServoChangeDoesNotRefreshUnrelatedStationTargets()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<OperationViewModel>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed && !state.IsRunning);
        view.Activate();
        var notifications = new ConcurrentQueue<string?>();
        view.PropertyChanged += (_, e) => notifications.Enqueue(e.PropertyName);
        try
        {
            motion.SetServo(MotionAxis.X, false);
            await WaitUntilAsync(() => !state.FeedbackReadiness.ServosOn
                && notifications.Contains(nameof(OperationViewModel.MachineDisplayState)));
            Assert.Equal(MachineDisplayState.ServoOff, view.MachineDisplayState);
            Assert.Contains(nameof(OperationViewModel.SupplyDisplayState), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.BoltTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.InspectionTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.ModeText), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.Alarm), notifications);
        }
        finally
        {
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task UnchangedFeedbackDoesNotRefreshTheViewAndInputChangesNotifyImmediately()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        var view = services.GetRequiredService<OperationViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        view.Activate();
        await view.LoadOlderPcbsCommand.ExecutionTask!;
        var notifications = 0;
        var samples = 0;
        void OnViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Interlocked.Increment(ref notifications);
        }
        void OnSampled(MotionGroup group, MotionFeedbackSample sample)
        {
            Interlocked.Increment(ref samples);
        }
        view.PropertyChanged += OnViewChanged;
        feedback.Sampled += OnSampled;
        try
        {
            await Task.Delay(650);
            Assert.True(Volatile.Read(ref samples) > 0);
            Assert.Equal(0, Volatile.Read(ref notifications));
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            Assert.True(Volatile.Read(ref notifications) > 0);
            Assert.True(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);

            await machine.StopAsync();
            var stopped = Volatile.Read(ref notifications);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            Assert.True(Volatile.Read(ref notifications) > stopped);
            Assert.False(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);
        }
        finally
        {
            view.PropertyChanged -= OnViewChanged;
            feedback.Sampled -= OnSampled;
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisplayUsesAcquiredFeedbackForAllStationsAndNeverFallsBackToHardware()
    {
        var settings = FlowSettings();
        var readingDisplay = new AsyncLocal<bool>();
        var unexpectedRead = new InvalidOperationException("Display attempted a hardware read.");
        void BeforeHardwareRead()
        {
            if (readingDisplay.Value)
                throw unexpectedRead;
        }

        await using var services = CreateMotionScopeServices(settings, out var probes, registrations =>
            registrations.AddSingleton<IIoService>(provider =>
            {
                var wrapper = System.Reflection.DispatchProxy.Create<IIoService, IoTests.OutputReadProbe>();
                var probe = (IoTests.OutputReadProbe)wrapper;
                probe.Io = provider.GetRequiredService<VirtualIoService>();
                probe.BeforeRead = BeforeHardwareRead;
                return wrapper;
            }));
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        foreach (var probe in probes.Values)
            probe.BeforeHardwareRead = BeforeHardwareRead;
        try
        {
            foreach (var station in new (InputIo Carrier, InputIo PlateDown, InputIo PlateUp,
                InputIo StopperUp, InputIo StopperDown, InputIo HeatSink)[]
            {
                (InputIo.PcbPlacementCarrierPresent,
                    InputIo.PcbPlacementBackupPlateDown, InputIo.PcbPlacementBackupPlateUp,
                    InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperDown,
                    InputIo.PcbPlacementHeatSink1Present),
                (InputIo.BoltFasteningCarrierPresent,
                    InputIo.BoltFasteningBackupPlateDown, InputIo.BoltFasteningBackupPlateUp,
                    InputIo.BoltFasteningStopperUp, InputIo.BoltFasteningStopperDown,
                    InputIo.BoltFasteningHeatSink1Present),
                (InputIo.InspectionCarrierPresent,
                    InputIo.InspectionBackupPlateDown, InputIo.InspectionBackupPlateUp,
                    InputIo.InspectionStopperUp, InputIo.InspectionStopperDown,
                    InputIo.InspectionHeatSink1Present),
            })
            {
                io.SetInput(station.Carrier, true);
                io.SetInput(station.PlateDown, false);
                io.SetInput(station.PlateUp, true);
                io.SetInput(station.StopperUp, false);
                io.SetInput(station.StopperDown, true);
                io.SetInput(station.HeatSink, true);
            }

            Assert.True(machine.TeachingReady);
            Assert.True(services.GetRequiredService<BoltFasteningStation>().Station.CarrierSeated);
            Assert.True(services.GetRequiredService<InspectionStation>().Station.CarrierSeated);
            state.AutomaticRunning = true;
            readingDisplay.Value = true;
            var display = services.GetRequiredService<OperationViewModel>();
            Assert.True(state.Available);
            // A machine-level flag cannot invent an executing step in an idle unit.
            Assert.Null(display.FasteningState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.BoltFasteningActiveBolt);
            Assert.Null(services.GetRequiredService<BoltFasteningStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActivePcb);
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<PcbSupplier>().Motion.IsAt(settings.PcbSupply.HandoffPosition)));
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(
                () => services.GetRequiredService<IIoService>().GetOutput(OutputIo.MainConveyorRun)));

            readingDisplay.Value = false;
            io.SetInput(InputIo.InspectionBackupPlateUp, false);
            io.SetInput(InputIo.InspectionBackupPlateDown, true);
            io.SetInput(InputIo.InspectionStopperDown, false);
            io.SetInput(InputIo.InspectionStopperUp, true);
            var work = services.GetRequiredService<InspectionStation>();
            work.RequestInspection(work.Station.CurrentJob);
            readingDisplay.Value = true;
            Assert.Null(display.ConveyorState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.InspectionActivePcb);
            _ = display.InspectionActiveBolt;

            // Unavailable sampled feedback must remain unknown instead of reading the SDK.
            services.GetRequiredService<PcbPlacer>().Motion.InvalidateFeedback(new IOException("Lost sample."));
            Assert.Null(display.PlacementState);
        }
        finally
        {
            readingDisplay.Value = false;
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionFeedbackFault.Alarm)]
    [InlineData(MotionFeedbackFault.ServoOff)]
    [InlineData(MotionFeedbackFault.HomeLost)]
    [InlineData(MotionFeedbackFault.ReadFailure)]
    public async Task AutomaticFeedbackStopsOnSilentMotionFaultWithoutAView(MotionFeedbackFault fault)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var active = probes[MotionGroup.InspectionGantry];
        foreach (var probe in probes.Where(item => item.Key != MotionGroup.InspectionGantry).Select(
            item => item.Value))
        {
            probe.ReportReady = true;
            probe.AllowStop = true;
            probe.FailHardwareCalls = true; // Disabled hardware must not be sampled, even while AUTO polls.
        }

        Task? run = null;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
                $"START blocked: {machine.StartBlock}; busy={state.IsRunning}; alarm={state.AlarmDetail}");
            run = machine.StartAsync();
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
                $"AUTO did not start: block={machine.StartBlock}, alarm={state.Alarm}. {state.AlarmDetail}");
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            if (fault == MotionFeedbackFault.ReadFailure)
                // Isolate the monitor: a simultaneous command read failure has its own unit alarm.
                active.DiagnosticReadError = new IOException("Unavailable diagnostic feedback.");
            else
                active.OverrideState = value => fault switch
                {
                    MotionFeedbackFault.Alarm => value with { Alarm = true },
                    MotionFeedbackFault.HomeLost => value with { Homed = false },
                    MotionFeedbackFault.ServoOff => value with { ServoOn = false },
                    _ => throw new ArgumentOutOfRangeException(nameof(fault)),
                };
            // No StateChanged, DI changes, UI timer or explicit refresh request accompanies this fault.
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
            await WaitUntilAsync(
                () => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.All(
                probes.Where(item => item.Key != MotionGroup.InspectionGantry),
                item => Assert.Equal(0, item.Value.HardwareCalls));
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            await WaitUntilAsync(() => !state.FeedbackReadiness.Faulted);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm); // Recovery never restarts AUTO.
        }
        finally
        {
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            // Shutdown verifies every axis, including disabled groups.
            foreach (var probe in probes.Values)
                probe.FailHardwareCalls = false;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HomeAndAutomaticStartIgnorePreStartSample()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var motion, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        Assert.False(state.FeedbackReadiness.Homed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var released = new ManualResetEventSlim();
        var blocked = 0;
        motion.AfterDiagnosticStateRead = axis =>
        {
            if (axis != MotionAxis.X || Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            entered.TrySetResult();
            released.Wait();
        };
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveAutomaticSample(MotionGroup group, MotionFeedbackSample sample)
        {
            if (state.AutomaticRunning
                && group == MotionGroup.InspectionGantry
                && sample.Readiness.Homed)
                sampled.TrySetResult();
        }

        Task? run = null;
        feedback.Sampled += ObserveAutomaticSample;
        try
        {
            // The X sample says not homed, but its delivery is delayed across Home and Start.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.HomeAsync(CancellationToken.None);
            Assert.False(machine.IsStartAllowed);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            released.Set();
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(state.FeedbackReadiness.Homed);
        }
        finally
        {
            released.Set();
            motion.AfterDiagnosticStateRead = null;
            feedback.Sampled -= ObserveAutomaticSample;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisplayReadFailureIsVisibleAndDoesNotReplaceLiveAdmissionChecks()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        Assert.True(state.Available);
        var view = services.GetRequiredService<OperationViewModel>();
        var messages = new ConcurrentQueue<string?>();
        var modes = new ConcurrentQueue<string>();
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.AlarmMessage))
                messages.Enqueue(view.AlarmMessage);
            if (e.PropertyName == nameof(OperationViewModel.ModeText))
                modes.Enqueue(view.ModeText);
        };
        var error = new IOException("Display feedback unavailable.");
        feedback.BeforeRead = () => throw error;
        feedback.DiagnosticReadError = error;
        // A ready display is not permission to operate when the actual read fails.
        Assert.Throws<IOException>(() => state.MotionReadiness);
        await WaitUntilAsync(() => !state.Available && messages.Contains(error.Message)
            && modes.Contains("UNKNOWN"));
        Assert.Same(error, state.ReadError);
        Assert.Equal(
            MachineDisplayState.Unavailable,
            view.ConveyorStatus);
        Assert.False(machine.IsHomeAllowed);
        Assert.False(machine.IsStartAllowed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.All(
            services.GetRequiredService<InspectionStation>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));

        var nextError = new IOException(error.Message, new InvalidOperationException());
        feedback.DiagnosticReadError = nextError;
        feedback.BeforeRead = () => throw nextError;
        await WaitUntilAsync(() => ReferenceEquals(nextError, state.ReadError));

        feedback.BeforeRead = null;
        feedback.DiagnosticReadError = null;
        await WaitUntilAsync(() => state.Available && messages.Contains(null)
            && modes.Contains("MANUAL"));
        Assert.Null(state.ReadError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualServoFailureStaysAtTheCommandBoundary(bool emergencyStop)
    {
        await using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var row = manual.Axes.Single(
            axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionStation>().Motion.Feedback;
        void FailOnce()
        {
            motion.StateChanged -= FailOnce;
            if (emergencyStop)
            {
                io.SetInput(InputIo.EmergencyStop1Pressed, true);
                Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            }

            throw new IOException("Servo feedback failed.");
        }

        motion.StateChanged += FailOnce;

        Assert.True(row.ToggleServoCommand.CanExecute(null));
        row.ToggleServoCommand.Execute(null);
        Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains("Servo feedback failed", state.AlarmDetail);
        await WaitUntilAsync(() => row.Diagnostics.Snapshot.State?.ServoOn == false);
        Assert.False(row.Diagnostics.Snapshot.State?.ServoOn);
    }

    [Fact]
    public async Task ManualCommandAfterShutdownDoesNotEscapeTheBoundary()
    {
        await using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplier>().Motion.Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

    public enum MotionFeedbackFault
    {
        Alarm,
        ServoOff,
        HomeLost,
        ReadFailure,
    }
}
