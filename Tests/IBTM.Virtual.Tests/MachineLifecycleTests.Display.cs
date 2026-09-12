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
    [Fact]
    public async Task DisplayReadsCoalesceWithoutBlockingViewsAndSurviveMachineStop()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var readingView = new AsyncLocal<bool>();
        var blocked = 0;
        var updates = 0;
        feedback.BeforeRead = () =>
        {
            Assert.False(readingView.Value);
            if (Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            entered.Set();
            released.Wait();
        };
        state.DisplayChanged += () => Interlocked.Increment(ref updates);
        try
        {
            state.RequestDisplayRefresh();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            await Task.Run(
                () =>
                {
                    readingView.Value = true;
                    var manual = services.GetRequiredService<MotionWindowViewModel>();
                    foreach (var row in manual.Axes)
                    {
                        _ = row.Diagnostics.Snapshot.Condition;
                        _ = manual.HomeAxisCommand.CanExecute(row);
                    }

                    var supply = services.GetRequiredService<SupplyTeachingViewModel>();
                    var station = services.GetRequiredService<StationTeachingViewModel>();
                    foreach (var unit in station.TeachingUnits.Prepend(HardwareArea.PcbSupply))
                    {
                        if (unit != HardwareArea.PcbSupply)
                            station.SelectedTeachingUnit = unit;
                        TeachingMotionViewModel teaching = unit == HardwareArea.PcbSupply ? supply : station;
                        _ = teaching.ManualBlock;
                        _ = teaching.CanEditTeaching;
                        _ = teaching.MotionHint;
                        foreach (var direction in Enum.GetValues<TeachingDirection>())
                        {
                            _ = teaching.JogCommand.CanExecute(direction);
                            _ = teaching.StepCommand.CanExecute(direction);
                        }

                        foreach (var output in teaching.TeachingOutputs.Values)
                            _ = teaching.ToggleOutputCommand.CanExecute(output);
                    }

                    for (var index = 0; index < 1000; index++)
                        state.RequestDisplayRefresh();
                })
                .WaitAsync(TimeSpan.FromSeconds(2));

            var io = services.GetRequiredService<VirtualIoService>();
            var light = services.GetRequiredService<IoSignals>().Outputs[OutputIo.MachineLight];
            Assert.False(light.IsOn);
            io.SetOutput(light.Signal, true);
            Assert.False(light.IsOn); // Display acquisition is still blocked.
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
        }
    }

    [Theory]
    [InlineData("Alarm")]
    [InlineData("ServoOff")]
    [InlineData("ReadFailure")]
    public async Task AutomaticPollingStopsOnSilentEnabledMotionFaultWithoutMonitorWindow(string fault)
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
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(machine.CanStart);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.Display.AutomaticRunning && state.Display.Homed);
            var scans = 0;
            void CountScan()
            {
                Interlocked.Increment(ref scans);
            }

            state.DisplayChanged += CountScan;
            try
            {
                await WaitUntilAsync(() => Volatile.Read(ref scans) >= 2);
                Assert.False(run.IsCompleted);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                if (fault == "ReadFailure")
                    active.FailHardwareCalls = true;
                else
                    active.OverrideState = value =>
                        fault == "Alarm"
                            ? value with { Alarm = true }

                            : value with { ServoOn = false };
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
                active.FailHardwareCalls = false;
                active.OverrideState = null;
                state.RequestDisplayRefresh();
                await WaitUntilAsync(() => state.Display.Available && !state.Display.MotionFaulted);
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm); // Recovery never restarts AUTO.
            }
            finally
            {
                state.DisplayChanged -= CountScan;
            }
        }
        finally
        {
            active.FailHardwareCalls = false;
            active.OverrideState = null;
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
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => state.Display.Available);
        Assert.Null(state.Display.ReadError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayProgrammingErrorsAreReportedAndNotRetried(bool duringInitialization)
    {
        var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var error = new InvalidOperationException("Display calculation failed.");
        try
        {
            if (!duringInitialization)
                await machine.InitializeAsync();
            feedback.BeforeRead = () => throw error;
            if (duringInitialization)
            {
                Assert.Same(
                    error,
                    await Assert.ThrowsAsync<InvalidOperationException>(
                        () => machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2))));
            }
            else
            {
                state.RequestDisplayRefresh();
                await WaitUntilAsync(() => ReferenceEquals(error, state.Display.ReadError));
            }

            feedback.BeforeRead = null;
            state.RequestDisplayRefresh();
            Assert.Same(
                error,
                await Assert.ThrowsAsync<InvalidOperationException>(machine.ShutdownAsync));
            Assert.Same(error, state.Display.ReadError);
        }
        finally
        {
            Assert.Same(error, Record.Exception(services.Dispose));
        }
    }

    [Fact]
    public async Task ManualServoFailureStaysAtTheCommandBoundary()
    {
        using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var row = manual.Axes.Single(
            axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionGantry>().Feedback;
        void FailOnce()
        {
            motion.StateChanged -= FailOnce;
            throw new IOException("Servo feedback failed.");
        }

        motion.StateChanged += FailOnce;

        Assert.True(manual.ToggleServoCommand.CanExecute(row));
        manual.ToggleServoCommand.Execute(row);
        Assert.Equal(MachineAlarm.MotionUnavailable, services.GetRequiredService<MachineState>().Alarm);
        Assert.Contains("Servo feedback failed", services.GetRequiredService<MachineState>().AlarmDetail);
        await WaitUntilAsync(() => row.Diagnostics.Snapshot.State?.ServoOn == false);
        Assert.False(row.Diagnostics.Snapshot.State?.ServoOn);
    }

    [Fact]
    public async Task ManualCommandAfterShutdownDoesNotEscapeTheBoundary()
    {
        using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplyHandler>().Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

}
