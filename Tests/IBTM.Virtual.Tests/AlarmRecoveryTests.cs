using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.AlphaMotion;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AlarmRecoveryTests
{
    [Fact]
    public async Task InterruptedConveyorStartsWithCarrierStillPresentWithoutReset()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            var run = conveyor.RunAsync();
            try
            {
                await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            }
            finally
            {
                conveyor.Stop();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            Assert.True(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
            var restarted = machine.StartAsync();
            try
            {
                await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.True(state.AutomaticRunning);
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }
            finally
            {
                machine.Stop();
                await restarted.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndicatorsChangeOnNotificationsNotDisplayRefreshOrNgMotorStop()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var ngConveyor = services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>();
        await machine.InitializeAsync();
        try
        {
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            Assert.Equal(OutputBlockReason.None, machine.ToggleDiagnosticOutput(OutputIo.Buzzer));
            Assert.Equal(OutputBlockReason.None, machine.ToggleDiagnosticOutput(OutputIo.TowerLampYellow));
            var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnDisplayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                refreshed.TrySetResult();
            }

            state.PropertyChanged += OnDisplayChanged;
            try
            {
                state.Refresh();
                await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.True(io.GetOutput(OutputIo.Buzzer));
                Assert.False(io.GetOutput(OutputIo.TowerLampYellow));
            }
            finally
            {
                state.PropertyChanged -= OnDisplayChanged;
            }

            machine.SilenceBuzzer();
            Assert.False(io.GetOutput(OutputIo.Buzzer));
            Assert.False(io.GetOutput(OutputIo.TowerLampYellow));
            io.SetInput(InputIo.AutoMode, false); // Selecting AUTO alone is not Auto Run.
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => state.AutoMode,
                TimeSpan.FromSeconds(2)));
            Assert.False(io.GetOutput(OutputIo.TowerLampYellow));
            state.AutomaticRunning = true;
            await AssertIndicatorsAsync(OutputIo.TowerLampGreen, false);

            state.SetError(MachineAlarm.MotionUnavailable);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            ngConveyor.Stop();
            Assert.True(io.GetOutput(OutputIo.Buzzer));
            Assert.False(machine.IsResetAllowed); // Auto Run blocks recovery, not acknowledgement.
            await machine.ResetAsync();
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, false);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            state.Refresh();
            Assert.False(io.GetOutput(OutputIo.Buzzer));
            state.SetError(MachineAlarm.Inspection);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            await machine.ResetAsync();
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, false);
            state.ClearError();
            await AssertIndicatorsAsync(OutputIo.TowerLampGreen, false);

            io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            Assert.True(ngConveyor.Full);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            io.SetInput(InputIo.ResetButton, true);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, false);
            io.SetInput(InputIo.ResetButton, false);
            Assert.True(ngConveyor.Full);
            state.AutomaticRunning = false;
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, false);
            io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
        }
        finally
        {
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }

        async Task AssertIndicatorsAsync(OutputIo lamp, bool buzzer)
        {
            OutputIo[] lamps = [OutputIo.TowerLampGreen, OutputIo.TowerLampYellow, OutputIo.TowerLampRed];
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => lamps.All(output => io.GetOutput(output) == (output == lamp))
                    && io.GetOutput(OutputIo.Buzzer) == buzzer,
                TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task IdleMotionFaultUpdatesIndicatorsWithoutAViewOrLatchedAlarm()
    {
        await using var services = CreateServices();
        services.GetRequiredService<MachineSettings>().Units.PcbSupply = true;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        try
        {
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            motion.SetAlarm(MotionAxis.X, true);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            Assert.True(state.FeedbackReadiness.Faulted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.AutomaticRunning);

            machine.SilenceBuzzer();
            // Other readiness changes must not acknowledge the fault or sound it again.
            motion.SetServo(MotionAxis.X, false);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => !state.FeedbackReadiness.ServosOn, TimeSpan.FromSeconds(2)));
            state.Refresh();
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, false);

            motion.SetAlarm(MotionAxis.X, false);
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            motion.SetAlarm(MotionAxis.X, true);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            await machine.ResetAsync();
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            Assert.False(state.FeedbackReadiness.Faulted);

            // Diagnostic faults on an unused unit must not change production indicators.
            var unused = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
            unused.SetAlarm(MotionAxis.X, true);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => services.GetRequiredService<MachineFeedbackMonitor>().Motions[MotionGroup.BoltFastening]
                    .MonitorAxes[MotionAxis.X].Sample.State is { Alarm: true },
                TimeSpan.FromSeconds(2)));
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
        }
        finally
        {
            await machine.ShutdownAsync();
        }

        async Task AssertIndicatorsAsync(OutputIo lamp, bool buzzer)
        {
            OutputIo[] lamps = [OutputIo.TowerLampGreen, OutputIo.TowerLampYellow, OutputIo.TowerLampRed];
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => lamps.All(output => io.GetOutput(output) == (output == lamp))
                    && io.GetOutput(OutputIo.Buzzer) == buzzer,
                TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task NgEjectionKeepsRedAndDoesNotRebuzzOnPassingSensors()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<NgCarrierConveyor>();
        await machine.InitializeAsync();
        services.GetRequiredService<MachineSettings>().Units.NgConveyor = true;
        services.GetRequiredService<MachineSettings>().NgConveyor.EjectRunSeconds = 0.1;
        io.AutoResponseEnabled = false;
        void ActuatorFeedback(OutputIo output, bool on)
        {
            if (output == OutputIo.NgShuttleDown)
                io.SetInputs((InputIo.NgShuttleDown, on), (InputIo.NgShuttleUp, !on));
            if (output == OutputIo.NgConveyorStopperUp)
                io.SetInputs((InputIo.NgConveyorStopperUp, on), (InputIo.NgConveyorStopperDown, !on));
        }
        io.OutputChanged += ActuatorFeedback;
        io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true), (InputIo.NgShuttleCarrierDetected, true));
        state.AutomaticRunning = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.NgCarrierEjectLamp, true);
            Assert.True(io.GetOutput(OutputIo.Buzzer));
            io.SetInput(InputIo.NgCarrierEjectButton, true);
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.NgConveyorRun, true);
            Assert.True(conveyor.IsEjectionPending);
            Assert.False(io.GetOutput(OutputIo.Buzzer));
            // Passing sensors must neither clear the red indication nor retrigger the buzzer.
            foreach (var input in new[] { InputIo.NgConveyorPosition1Occupied, InputIo.NgConveyorPosition2Occupied })
            {
                io.SetInput(input, false);
                Assert.True(io.GetOutput(OutputIo.TowerLampRed));
                io.SetInput(input, true);
                Assert.False(io.GetOutput(OutputIo.Buzzer));
                Assert.True(io.GetOutput(OutputIo.TowerLampRed));
            }
            state.SetError(MachineAlarm.MotionUnavailable);
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.Buzzer, true);
            state.ClearError();
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.Buzzer, false);
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.NgCarrierEjectCompleteLamp, true);
            io.SetInput(InputIo.NgCarrierEjectButton, false);
            io.SetInputs((InputIo.NgConveyorPosition1Occupied, false),
                (InputIo.NgConveyorPosition2Occupied, false), (InputIo.NgShuttleCarrierDetected, false));
            Assert.True(io.GetOutput(OutputIo.TowerLampRed));
            Assert.False(io.GetOutput(OutputIo.Buzzer));
            io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
            io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.TowerLampGreen, true);
            Assert.False(conveyor.IsEjectionPending);
            Assert.False(io.GetOutput(OutputIo.TowerLampRed));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            io.OutputChanged -= ActuatorFeedback;
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DirectOutputsChangeOnlyTheSelectedSignalWithoutSetupOrFeedbackAdmission()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            services.GetRequiredService<MachineSettings>().Units.MainConveyor = false;
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            io.SetInput(InputIo.NgCarrierPickupUp, false);
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            SetAlarm(state, MachineAlarm.MainConveyor);
            using var otherOperation = services.GetRequiredService<OperationCancellation>().Link();
            var writes = new System.Collections.Generic.List<(OutputIo Signal, bool On)>();
            io.OutputChanged += (signal, on) => writes.Add((signal, on));

            foreach (var signal in signals.Outputs.Values)
            {
                var output = signal.Signal;
                io.SetOutput(output, false);
                writes.Clear();
                var row = new OutputSignalRow(signal, machine);
                if (output == OutputIo.PcbPlacementHandlerRotate)
                {
                    Assert.False(row.ToggleCommand.CanExecute(null));
                    Assert.Equal(OutputBlockReason.None, machine.ToggleDiagnosticOutput(output));
                    Assert.False(io.GetOutput(output));
                    Assert.Empty(writes);
                    continue;
                }
                row.ToggleCommand.Execute(null);
                Assert.True(io.GetOutput(output));
                // Normal outputs toggle without a feedback wait or display refresh.
                Assert.True(row.ToggleCommand.CanExecute(null));
                row.ToggleCommand.Execute(null);
                var fixedOn = output is OutputIo.MainConveyorNormalSpeed or OutputIo.NgConveyorNormalSpeed;
                Assert.Equal(fixedOn, io.GetOutput(output));
                Assert.Null(row.ActionMessage);
                Assert.Equal(fixedOn ? [(output, true)] : new[] { (output, true), (output, false) }, writes);
            }

            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DirectRunOutputsStillStopOnAutoAndEmergencyStop()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            var rows = new[]
            {
                OutputIo.MainConveyorRun,
                OutputIo.NgConveyorRun,
                OutputIo.ShootBolt,
                OutputIo.MainConveyorReadyToFront2
            }.Select(output => new OutputSignalRow(signals.Outputs[output], machine))
                .ToArray();
            foreach (var row in rows)
                row.ToggleCommand.Execute(null);
            Assert.All(rows, row => Assert.True(io.GetOutput(row.Io.Signal)));

            io.SetInput(InputIo.AutoMode, false);
            Assert.All(
                rows,
                row =>
                {
                    Assert.False(io.GetOutput(row.Io.Signal));
                    row.ToggleCommand.Execute(null);
                    Assert.Equal(OutputBlockReason.AutoMode.GetDescription(), row.ActionMessage);
                    Assert.False(io.GetOutput(row.Io.Signal));
                });

            io.SetInput(InputIo.AutoMode, true);
            foreach (var row in rows)
                row.ToggleCommand.Execute(null);
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            Assert.All(
                rows,
                row =>
                {
                    Assert.False(io.GetOutput(row.Io.Signal));
                    row.ToggleCommand.Execute(null);
                    Assert.Equal(OutputBlockReason.EmergencyStop.GetDescription(), row.ActionMessage);
                    Assert.False(io.GetOutput(row.Io.Signal));
                });
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task OutputOffDoesNotRequireOnAdmissionOrOwnership()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            services.GetRequiredService<MachineSettings>().Units.MainConveyor = false;
            foreach (var output in new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun })
            {
                var row = new ManualConveyorRow(signals.Outputs[output], machine);
                io.SetOutput(output, true);
                signals.RefreshOutputs();
                Assert.False(row.RunCommand.IsRunning);
                Assert.True(row.StopCommand.CanExecute(null));
                await row.StopCommand.ExecuteAsync(null);
                Assert.False(io.GetOutput(output));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DiagnosticMainConveyorRunsUntilStopAutoOrWindowCancellation()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        Assert.True(io.GetOutput(OutputIo.MainConveyorForward));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => state.Alarm == MachineAlarm.MotionUnavailable,
                    TimeSpan.FromSeconds(2)));
            var row = new ManualConveyorRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MainConveyorRun],
                machine);

            foreach (var stop in new Action[]
            {
                () => row.StopCommand.Execute(null), // STOP remains available while busy.
                machine.Stop,
                () => io.SetInput(InputIo.AutoMode, false),
                row.RunCommand.Cancel, // Leaving Manual uses this cancellation.
            })
            {
                io.SetInput(InputIo.AutoMode, true);
                Assert.False(row.RunCommand.IsRunning);
                var run = row.RunCommand.ExecuteAsync(null);
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => io.GetOutput(OutputIo.MainConveyorRun),
                        TimeSpan.FromSeconds(2)));
                Assert.True(state.IsRunning);
                Assert.True(io.GetOutput(OutputIo.MainConveyorForward));
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
                Assert.True(row.StopCommand.CanExecute(null));
                stop();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(state.IsRunning);
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun)]
    [InlineData(OutputIo.NgConveyorRun)]
    public async Task ManualAndOutputsShareConveyorControlAndCanStopEachOther(OutputIo output)
    {
        await using var services = CreateServices();
        services.GetRequiredService<MachineSettings>().Units.NgConveyor = true;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            Assert.Equal(
                new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun },
                manual.Conveyors.Select(row => row.Io.Signal));
            var manualRow = manual.Conveyors.Single(row => row.Io.Signal == output);
            var outputRow = new OutputSignalRow(signals.Outputs[output], machine);

            var run = manualRow.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            outputRow.ToggleCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(output));
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => !state.IsRunning, TimeSpan.FromSeconds(2)));

            outputRow.ToggleCommand.Execute(null);
            Assert.True(io.GetOutput(output));
            Assert.True(manualRow.StopCommand.CanExecute(null));
            await manualRow.StopCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(output));
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => !state.IsRunning, TimeSpan.FromSeconds(2)));

            var ownedRun = manualRow.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            await manual.ShutdownAsync(); // Application shutdown still stops owned runs.
            await ownedRun.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(output));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        }
        finally
        {
            await manual.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgMotorRunDoesNotUseCarrierOrShuttlePositionAsAdmission()
    {
        await using var services = CreateServices();
        services.GetRequiredService<MachineSettings>().Units.NgConveyor = true;
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            var row = new ManualConveyorRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.NgConveyorRun],
                machine);
            var shuttle = io.GetOutput(OutputIo.NgShuttleDown);
            var stopper = io.GetOutput(OutputIo.NgConveyorStopperUp);
            foreach (var up in new[] { true, false })
            {
                io.SetInput(InputIo.NgShuttleUp, up);
                io.SetInput(InputIo.NgShuttleDown, !up);
                var run = row.RunCommand.ExecuteAsync(null);
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => io.GetOutput(OutputIo.NgConveyorRun),
                        TimeSpan.FromSeconds(2)));
                Assert.False(io.GetOutput(OutputIo.NgConveyorReverse));
                Assert.Equal(shuttle, io.GetOutput(OutputIo.NgShuttleDown));
                Assert.Equal(stopper, io.GetOutput(OutputIo.NgConveyorStopperUp));
                // First pass: carriers appear while running. Second pass: start
                // with every carrier sensor already ON. Neither starts a sequence.
                io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
                io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                state.Refresh();
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => state.Available,
                        TimeSpan.FromSeconds(2)));
                Assert.True(io.GetOutput(OutputIo.NgConveyorRun));
                Assert.False(run.IsCompleted);
                row.RunCommand.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            }

            var active = row.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.NgConveyorRun));
            // Removing occupancy admission does not remove the MANUAL-only gate.
            io.SetInput(InputIo.AutoMode, false);
            await active.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            await row.RunCommand.ExecuteAsync(null);
            Assert.Equal(OutputBlockReason.AutoMode.GetDescription(), row.ActionMessage);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(state.IsRunning);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ModeSelectorUsesManualOnAndAutoOffWithoutChangingRawIo()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var input = services.GetRequiredService<IoSignals>().Inputs[InputIo.AutoMode];
        await machine.InitializeAsync();
        try
        {
            Assert.True(io.GetInput(InputIo.AutoMode));
            Assert.True(input.IsOn);
            Assert.True(state.ManualMode);
            Assert.False(state.AutoMode);
            Assert.True(view.IsSettingsEditAllowed);

            io.SetInput(InputIo.AutoMode, false);
            Assert.False(input.IsOn);
            Assert.True(state.AutoMode);
            Assert.False(state.ManualMode);
            Assert.False(view.IsSettingsEditAllowed);
            Assert.False(state.ManualSetupEnabled);
            Assert.False(state.AutomaticRunning);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => state.AutoMode, TimeSpan.FromSeconds(2)));

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(input.IsOn);
            Assert.False(state.AutoMode);
            Assert.True(state.ManualMode);
            Assert.True(view.IsSettingsEditAllowed);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => !state.AutoMode, TimeSpan.FromSeconds(2)));

            io.IsReady = false;
            Assert.Null(input.IsOn);
            Assert.False(state.AutoMode);
            Assert.False(state.ManualMode);
            Assert.False(state.ManualSetupEnabled);
            Assert.True(view.IsSettingsEditAllowed);
            Assert.False(view.IsTestLightAllowed);
            // Even when safety checks are bypassed, missing selector feedback is not MANUAL.
            var options = services.GetRequiredService<MachineOptions>();
            options.UseEmergencyStop = false;
            options.UseAirPressureInterlock = false;
            Assert.False(machine.IsUseAdcProtocolAllowed);
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.RecipeEditor.Name = "Offline recipe";
            Assert.True(teaching.IsSaveAllowed);
            await teaching.SaveCommand.ExecuteAsync(null);
            Assert.Null(teaching.SaveError);
            Assert.Contains("Offline recipe", services.GetRequiredService<MachineStore>().RecipeNames);
            io.IsReady = true;
            Assert.True(state.ManualMode);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AlarmSettingsRequireManualModeAndSavingDoesNotClearTheAlarm()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.AutoMode, false);
            Assert.False(view.IsSettingsEditAllowed);
            SetAlarm(state, MachineAlarm.Inspection);
            Assert.False(state.IsRunning);
            Assert.True(state.AutoMode);
            Assert.False(view.IsSettingsEditAllowed);
            Assert.False(view.IsSettingsEditAllowed);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(view.IsSettingsEditAllowed);
            Assert.True(view.IsSettingsEditAllowed);
            Assert.True(state.ManualSetupEnabled);
            Assert.False(machine.IsStartAllowed);
            Assert.False(machine.IsHomeAllowed);

            var input = view.InputMappingView.Cast<HardwareMappingRow>()
                .Single(row => row.Signal.Equals(InputIo.PcbPlacementStopperUp));
            var runningInputs = services.GetRequiredService<IReadOnlyDictionary<InputIo, int>>();
            var originalInput = runningInputs[InputIo.PcbPlacementStopperUp];
            input.Number = 63;
            Assert.Equal(63, view.Settings.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp]);
            Assert.Equal(originalInput, runningInputs[InputIo.PcbPlacementStopperUp]);
            var output = view.OutputMappingView.Cast<HardwareMappingRow>().Single(
                row => row.Signal.Equals(OutputIo.PcbPlacementStopperUp)).Output!;
            var axis = view.AxisMappings.Single(
                row => row.Signal.Equals(MachineAxis.InspectionGantryX)).Axis!;
            Assert.Same(view.Settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp], output);
            Assert.Same(view.Settings.InspectionGantryHardware.Axes[MachineAxis.InspectionGantryX], axis);
            var runningOutput = services.GetRequiredService<IReadOnlyDictionary<OutputIo, OutputHardware>>()
                [OutputIo.PcbPlacementStopperUp];
            var originalOutput = (runningOutput.Number, runningOutput.OffNumber, runningOutput.Feedback!.OnInput);
            output.Number = 80;
            output.OffNumber = 81;
            axis.Number = 12;

            view.Settings.AlphaMotion.ControllerNumber = 3;
            var motion = view.Settings.InspectionGantry.Motion;
            var speed = motion.HorizontalSpeed;
            motion.HorizontalSpeed = 0;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Contains(nameof(motion.HorizontalSpeed), view.DatabaseMessage);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);
            motion.HorizontalSpeed = double.NaN;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.StartsWith("Settings not saved:", view.DatabaseMessage);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);
            Assert.True(view.IsSettingsEditAllowed);

            motion.HorizontalSpeed = speed;
            var fasteningTimeout = view.Settings.Hantas.FasteningTimeoutMilliseconds;
            view.Settings.Hantas.FasteningTimeoutMilliseconds = -1;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Contains("Fastening timeout", view.DatabaseMessage);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);
            view.Settings.Hantas.FasteningTimeoutMilliseconds = fasteningTimeout;
            var responseTimeout = view.Settings.Hantas.ResponseTimeoutMilliseconds;
            view.Settings.Hantas.ResponseTimeoutMilliseconds = -1;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Contains("ADC response timeout", view.DatabaseMessage);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);
            view.Settings.Hantas.ResponseTimeoutMilliseconds = responseTimeout;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.StartsWith("Settings saved.", view.DatabaseMessage);
            Assert.Equal(
                3,
                services.GetRequiredService<MachineStore>()
                    .LoadSettings()
                    .Get<AlphaMotionSettings>()
                    .ControllerNumber);
            var saved = services.GetRequiredService<MachineStore>().LoadSettings();
            Assert.Equal(63, saved.Get<ConveyorHardwareSettings>().Inputs[InputIo.PcbPlacementStopperUp]);
            Assert.Equal(originalInput, runningInputs[InputIo.PcbPlacementStopperUp]);
            var savedOutput = saved.Get<ConveyorHardwareSettings>().Outputs[OutputIo.PcbPlacementStopperUp];
            var savedAxis = saved.Get<InspectionGantryHardwareSettings>().Axes[MachineAxis.InspectionGantryX];
            Assert.Equal((80, (int?)81, InputIo.PcbPlacementStopperUp),
                (savedOutput.Number, savedOutput.OffNumber, savedOutput.Feedback!.OnInput));
            Assert.Equal(12, savedAxis.Number);
            Assert.Equal(originalOutput,
                (runningOutput.Number, runningOutput.OffNumber, runningOutput.Feedback.OnInput));
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.False(io.GetInput(InputIo.ResetButton));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SettingsSaveKeepsEditedIoAfterRestartAndIsNotCancelledByEquipmentStop()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var store = services.GetRequiredService<MachineStore>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        try
        {
            var inputs = view.InputMappingView.Cast<HardwareMappingRow>();
            inputs.Single(row => row.Signal.Equals(InputIo.PcbPlacementStopperUp)).Number = 57;
            inputs.Single(row => row.Signal.Equals(InputIo.PcbPlacementStopperDown)).Number = 58;
            var output = view.OutputMappingView.Cast<HardwareMappingRow>()
                .Single(row => row.Signal.Equals(OutputIo.PcbPlacementStopperUp)).Output!;
            output.Number = 96;
            output.OffNumber = 97;
            view.Settings.Lighting.Connection = "";

            Task saving;
            using (var connection = new SqliteConnection($"Data Source={store.DatabaseFile}"))
            {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                saving = view.SaveSettingsCommand.ExecuteAsync(null);
                try
                {
                    Assert.True(view.SaveSettingsCommand.IsRunning);
                    Assert.False(view.IsSettingsEditAllowed);
                    Assert.False(operations.HasActiveOperations);
                    Assert.False(state.IsRunning);
                    operations.Cancel();
                }
                finally
                {
                    transaction.Commit();
                }
            }

            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.StartsWith("Settings saved.", view.DatabaseMessage);
            Assert.True(view.IsSettingsEditAllowed);
            var loaded = await MachineSettings.LoadAsync(new MachineStore(store.DatabaseFile));
            Assert.Equal(57, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperUp]);
            Assert.Equal(58, loaded.ConveyorHardware.Inputs[InputIo.PcbPlacementStopperDown]);
            var savedOutput = loaded.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp];
            Assert.Equal(96, savedOutput.Number);
            Assert.Equal(97, savedOutput.OffNumber);
            Assert.Equal("", loaded.Lighting.Connection);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SettingsCommandsStayDisabledWhileBusyOrClosingEvenWithAnAlarm()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        await machine.InitializeAsync();
        SetAlarm(state, MachineAlarm.Inspection);
        try
        {
            using (services.GetRequiredService<OperationCancellation>().Link())
            {
                Assert.False(view.IsSettingsEditAllowed);
                Assert.False(view.IsSettingsEditAllowed);
            }

            Assert.True(view.IsSettingsEditAllowed);
            Assert.True(view.IsSettingsEditAllowed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }

        Assert.False(view.IsSettingsEditAllowed);
        Assert.False(view.IsSettingsEditAllowed);
    }

    [Fact]
    public async Task SoftwareResetWorksWithoutPhysicalResetInputAndKeepsAutoSettingsLocked()
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<MachineOptions>().UseResetButton = false;
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.AutoMode, false);
            SetAlarm(state, MachineAlarm.Inspection);
            Assert.False(view.IsSettingsEditAllowed);
            Assert.True(machine.IsResetAllowed);
            await machine.ResetAsync();
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(view.IsSettingsEditAllowed);
            Assert.False(io.GetInput(InputIo.ResetButton));
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsHoming);
            Assert.False(state.IsRunning);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(InputIo.EmergencyStop1Pressed)]
    [InlineData(InputIo.Door1Open)]
    [InlineData(InputIo.Door2Open)]
    [InlineData(InputIo.Door3Open)]
    [InlineData(InputIo.Door4Open)]
    [InlineData(InputIo.Door5Open)]
    [InlineData(InputIo.Door6Open)]
    [InlineData(InputIo.AirPressureHigh)]
    public async Task SoftwareResetCannotClearAnActiveAutoSafetyFault(InputIo input)
    {
        await using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            var isDoor = input is InputIo.Door1Open
                or InputIo.Door2Open
                or InputIo.Door3Open
                or InputIo.Door4Open
                or InputIo.Door5Open
                or InputIo.Door6Open;
            Assert.True(state.DoorClosed);
            io.SetInput(InputIo.AutoMode, false);
            io.SetInput(input, input == InputIo.EmergencyStop1Pressed);
            var alarm = state.Alarm;
            Assert.NotEqual(MachineAlarm.None, alarm);
            Assert.False(machine.IsResetAllowed);
            await machine.ResetAsync();
            Assert.Equal(alarm, state.Alarm);
            Assert.False(state.ManualSetupEnabled);

            if (isDoor)
            {
                var signal = services.GetRequiredService<IoSignals>().Inputs[input];
                Assert.Equal(MachineAlarm.DoorOpen, alarm);
                Assert.Contains("Closed", input.GetDescription());
                Assert.False(signal.IsOn);
                Assert.False(state.DoorClosed);
                Assert.False(state.DoorInterlockReady);
                Assert.False(machine.IsStartAllowed);
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => state.Available && !state.DoorClosed,
                        TimeSpan.FromSeconds(2)));

                io.SetInput(input, true);
                Assert.True(signal.IsOn);
                Assert.True(state.DoorClosed);
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => state.DoorClosed,
                        TimeSpan.FromSeconds(2)));
                // Closing the door does not clear the latched alarm or restart the machine.
                Assert.Equal(alarm, state.Alarm);
                Assert.False(state.AutomaticRunning);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    private static void SetAlarm(MachineState state, MachineAlarm alarm)
    {
        state.SetError(alarm, new IOException("Simulated commissioning alarm."));
    }

    private static ServiceProvider CreateServices()
    {
        return new ServiceCollection().AddSingleton(
            VirtualTestSupport.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-alarm-recovery-{Guid.NewGuid():N}.db")))
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
                })
            .BuildServiceProvider();
    }
}
