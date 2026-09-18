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

        using var services = CreateMotionScopeServices(settings, out var probes, registrations =>
            registrations.AddSingleton<IIoService>(provider =>
            {
                var wrapper = System.Reflection.DispatchProxy.Create<IIoService, IoTests.OutputReadProbe>();
                var probe = (IoTests.OutputReadProbe)wrapper;
                probe.Io = provider.GetRequiredService<VirtualIoService>();
                probe.BeforeRead = BeforeHardwareRead;
                return wrapper;
            }));
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Display.Homed);
        await state.StopDisplayUpdatesAsync();
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        foreach (var probe in probes.Values)
            probe.BeforeHardwareRead = BeforeHardwareRead;
        try
        {
            foreach (var station in new[] { "PcbPlacement", "BoltFastening", "Inspection" })
            {
                io.SetInput(Enum.Parse<InputIo>(station + "CarrierPresent"), true);
                io.SetInput(Enum.Parse<InputIo>(station + "BackupPlateDown"), false);
                io.SetInput(Enum.Parse<InputIo>(station + "BackupPlateUp"), true);
                io.SetInput(Enum.Parse<InputIo>(station + "StopperUp"), false);
                io.SetInput(Enum.Parse<InputIo>(station + "StopperDown"), true);
                io.SetInput(Enum.Parse<InputIo>(station + "HeatSink1Present"), true);
            }

            Assert.True(machine.TeachingReady);
            Assert.True(services.GetRequiredService<BoltFasteningWork>().CarrierSeated);
            Assert.True(services.GetRequiredService<InspectionWork>().CarrierSeated);
            state.SetAutomaticRunning(true);
            readingDisplay.Value = true;
            var display = machine.ReadDisplay();
            Assert.True(display.Available);
            Assert.NotEqual(BoltFasteningState.Waiting, display.FasteningState);
            Assert.NotEqual(InspectionStationState.Waiting, display.InspectionState);
            Assert.NotNull(display.FasteningBolt);
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(() => state.Buffer.HasConflict()));
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(
                () => services.GetRequiredService<MainConveyor>().RunCommandOn));

            // Unavailable sampled feedback must remain unknown instead of reading the SDK.
            services.GetRequiredService<PcbPlacementHandler>().Motion.InvalidateFeedback(new IOException("Lost sample."));
            Assert.Throws<IOException>(machine.ReadDisplay);
        }
        finally
        {
            readingDisplay.Value = false;
            state.SetAutomaticRunning(false);
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisplayReadsCoalesceWithoutBlockingViewsAndSurviveMachineStop()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var readingView = new AsyncLocal<bool>();
        var blocked = 0;
        var updates = 0;
        feedback.BeforeRead = () =>
        {
            Assert.False(readingView.Value);
        };
        void OnDisplayChanged()
        {
            Interlocked.Increment(ref updates);
            if (Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            entered.Set();
            released.Wait();
        }

        state.DisplayChanged += OnDisplayChanged;
        try
        {
            state.RequestDisplayRefresh();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            await Task.Run(
                () =>
                {
                    readingView.Value = true;
                    var motion = services.GetRequiredService<InspectionGantry>().Motion;
                    foreach (var axis in motion.Axes.Values)
                    {
                        _ = axis.Condition;
                        _ = axis.ServoOn;
                    }

                    _ = motion.Position;
                    _ = state.Display.CanStart;
                    _ = state.Display.ManualBlock;

                    for (var index = 0; index < 1000; index++)
                        state.RequestDisplayRefresh();
                })
                .WaitAsync(TimeSpan.FromSeconds(2));

            var io = services.GetRequiredService<VirtualIoService>();
            var light = services.GetRequiredService<IoSignals>().Outputs[OutputIo.MachineLight];
            Assert.False(light.IsOn);
            io.SetOutput(light.Signal, true);
            Assert.False(light.IsOn); // Feedback acquisition was stopped above.
            var row = new OutputWindowRow(light, machine);
            row.ToggleCommand.Execute(null);
            Assert.False(io.GetOutput(light.Signal)); // Toggle the real ON, not the displayed OFF.

            released.Set();
            await WaitUntilAsync(() => Volatile.Read(ref updates) >= 2);
            await Task.Delay(30);
            Assert.Equal(2, Volatile.Read(ref updates));

            var previous = state.Display;
            machine.Stop();
            state.RequestDisplayRefresh();
            await WaitUntilAsync(() => !ReferenceEquals(previous, state.Display));
            await machine.ShutdownAsync();
            previous = state.Display;
            state.RequestDisplayRefresh();
            await Task.Delay(30);
            Assert.Same(previous, state.Display);
        }
        finally
        {
            released.Set();
            await machine.ShutdownAsync();
            state.DisplayChanged -= OnDisplayChanged;
        }
    }

    [Theory]
    [InlineData("Alarm")]
    [InlineData("ServoOff")]
    [InlineData("HomeLost")]
    [InlineData("ReadFailure")]
    public async Task AutomaticFeedbackStopsOnSilentMotionFaultAfterDisplayStops(string fault)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        settings.Units.MainConveyor = true;
        using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var active = probes[MotionGroup.InspectionGantry];
        foreach (var probe in probes.Where(item => item.Key != MotionGroup.InspectionGantry).Select(
            item => item.Value))
        {
            probe.ReportReady = true;
            probe.FailHardwareCalls = true; // Disabled hardware must not be sampled, even while AUTO polls.
        }

        Task? run = null;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(machine.CanStart);
            run = machine.StartAsync();
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
                $"AUTO did not start: block={machine.StartBlock}, alarm={state.Alarm}. {state.AlarmDetail}");
            await state.StopDisplayUpdatesAsync();
            var display = state.Display;
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            if (fault == "ReadFailure")
                // Isolate the monitor: a simultaneous command read failure has its own unit alarm.
                active.DiagnosticReadError = new IOException("Unavailable diagnostic feedback.");
            else
                active.OverrideState = value => fault switch
                {
                    "Alarm" => value with { Alarm = true },
                    "HomeLost" => value with { Homed = false },
                    _ => value with { ServoOn = false },
                };
            // No StateChanged, DI changes, UI timer or explicit refresh request accompanies this fault.
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.Same(display, state.Display);
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
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HomeAndAutomaticStartIgnorePreStartSampleAndStoppedDisplay()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateDisplayServices(out var motion, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        Assert.False(state.Display.Homed);
        await state.StopDisplayUpdatesAsync();
        var display = state.Display;
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
            await WaitUntilAsync(() => machine.CanStart);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            released.Set();
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Same(display, state.Display);
            Assert.False(state.Display.Homed);
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
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        Assert.True(state.Display.Available);
        var error = new IOException("Display feedback unavailable.");
        feedback.BeforeRead = () => throw error;
        feedback.DiagnosticReadError = error;
        // A ready display is not permission to operate when the actual read fails.
        Assert.Throws<IOException>(() => machine.CanHome);
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => !state.Display.Available);
        Assert.Same(error, state.Display.ReadError);
        Assert.False(state.Display.CanHome);
        Assert.False(state.Display.CanStart);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.All(
            services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));

        feedback.BeforeRead = null;
        feedback.DiagnosticReadError = null;
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => state.Display.Available);
        Assert.Null(state.Display.ReadError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayProgrammingErrorsAreReportedAndNotRetried(bool duringInitialization)
    {
        var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var error = new InvalidOperationException("Display calculation failed.");
        var fail = duringInitialization;
        var reads = 0;
        MachineDisplay ReadDisplay()
        {
            Interlocked.Increment(ref reads);
            return fail ? throw error : new MachineDisplay();
        }

        try
        {
            if (!duringInitialization)
                await state.StartDisplayUpdatesAsync(ReadDisplay);
            fail = true;
            if (duringInitialization)
            {
                Assert.Same(
                    error,
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => state.StartDisplayUpdatesAsync(ReadDisplay).WaitAsync(TimeSpan.FromSeconds(2))));
            }
            else
            {
                state.RequestDisplayRefresh();
                await WaitUntilAsync(() => ReferenceEquals(error, state.Display.ReadError));
            }

            var failedReads = Volatile.Read(ref reads);
            fail = false;
            state.RequestDisplayRefresh();
            Assert.Same(
                error,
                await Assert.ThrowsAsync<InvalidOperationException>(machine.ShutdownAsync));
            Assert.Same(error, state.Display.ReadError);
            Assert.Equal(failedReads, Volatile.Read(ref reads));
        }
        finally
        {
            Assert.Same(error, Record.Exception(services.Dispose));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualServoFailureStaysAtTheCommandBoundary(bool emergencyStop)
    {
        using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var row = manual.Axes.Single(
            axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionGantry>().Feedback;
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
        using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplyHandler>().Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

}
