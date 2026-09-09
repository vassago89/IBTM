using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IBTM.AlphaMotion;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AlarmRecoveryTests
{
    [Fact]
    public async Task OutputCommandsToggleObservedOutputState()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        Assert.Equal(OutputBlockReason.StateUnavailable, new MachineDisplay().ManualOutputBlock);
        Assert.False(new MachineDisplay().MainConveyorPathClear);
        foreach (var reason in Enum.GetValues<OutputBlockReason>())
        {
            if (reason == OutputBlockReason.None) continue;
            Assert.False(string.IsNullOrWhiteSpace(reason.GetDescription()));
            Assert.NotEqual(reason.ToString(), reason.GetDescription());
        }
        await machine.InitializeAsync();
        try
        {
            var row = new OutputControlRow(signals.Outputs[OutputIo.MachineLight], machine);
            io.SetOutput(OutputIo.MachineLight, false);
            signals.RefreshOutputs();
            Assert.False(row.Io.IsOn);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.MachineLight));
            signals.RefreshOutputs();
            Assert.True(row.Io.IsOn);
            await row.ToggleCommand.ExecuteAsync(null);
            signals.RefreshOutputs();
            Assert.False(row.Io.IsOn);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task InterfaceOutputsIgnoreWholeMachineMotionReadinessButKeepTransferInterlocks()
    {
        using var services = CreateServices();
        var settings = services.GetRequiredService<MachineSettings>();
        settings.Units.PcbSupply = true;
        settings.Units.NgCarrierTransfer = true;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            motion.SetServo(MotionAxis.X, false);
            motion.SetAlarm(MotionAxis.X, true);
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            Assert.False(state.Ready);
            Assert.False(state.Homed);

            foreach (var output in new[] { OutputIo.PcbSupplyReadyToFront1,
                OutputIo.MainConveyorReadyToFront2, OutputIo.MainConveyorAvailableToRear })
            {
                Assert.True(await VirtualTest.WaitUntilAsync(() => state.Display.Available
                    && !state.Display.IsRunning && state.Display.Alarm == MachineAlarm.MotionUnavailable,
                    TimeSpan.FromSeconds(2)));
                var row = new OutputControlRow(signals.Outputs[output], machine);
                Assert.True(row.ToggleCommand.CanExecute(null));
                var test = row.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(output), TimeSpan.FromSeconds(2)));
                Assert.True(row.StopOutputTestCommand.CanExecute(null));
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                row.StopOutputTestCommand.Execute(null);
                await test.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(output));
            }

            var ready = new OutputControlRow(signals.Outputs[OutputIo.MainConveyorReadyToFront2], machine);
            foreach (var input in new[] { InputIo.NgCarrierPickupDown,
                InputIo.MainConveyorEntryCarrierDetected, InputIo.MainConveyorReadyFromRear,
                InputIo.EmergencyStop1Pressed })
            {
                // Losing a real interlock while ON must still cancel and send OFF.
                var test = ready.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(OutputIo.MainConveyorReadyToFront2),
                    TimeSpan.FromSeconds(2)));
                io.SetInput(input, true);
                await test.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
                // A direct invocation cannot bypass the same blocked condition.
                await ready.ToggleCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
                io.SetInput(input, false);
                SetAlarm(state, MachineAlarm.MotionUnavailable);
            }
            io.SetInput(InputIo.AutoMode, false);
            await ready.ToggleCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(MachineAlarm.MotionUnavailable)]
    [InlineData(MachineAlarm.HomeFailed)]
    [InlineData(MachineAlarm.Inspection)]
    public async Task DiagnosticOutputsAllowNonSafetyAlarmsWithoutClearingThem(MachineAlarm alarm)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            SetAlarm(state, alarm);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.Alarm == alarm, TimeSpan.FromSeconds(2)));
            var row = new OutputControlRow(signals.Outputs[OutputIo.MachineLight], machine);
            Assert.True(row.ToggleCommand.CanExecute(null));
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.MachineLight));
            Assert.Equal(alarm, state.Alarm);
            Assert.False(state.ManualControlsEnabled);
            Assert.False(state.ManualSetupEnabled); // Teaching/automatic admission is unchanged.
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !state.Display.IsRunning, TimeSpan.FromSeconds(2)));

            foreach (var output in new[] { OutputIo.ShootBolt, OutputIo.NgShuttleDown })
            {
                var restricted = new OutputControlRow(signals.Outputs[output], machine);
                Assert.False(restricted.ToggleCommand.CanExecute(null));
                Assert.Contains("dedicated", restricted.ToggleHint);
                await restricted.ToggleCommand.ExecuteAsync(null);
                Assert.False(io.GetOutput(output));
            }

            // Bypassed general-machine checks still cannot energize diagnostic outputs
            // with an actual emergency stop or low-air input present.
            var options = services.GetRequiredService<MachineOptions>();
            options.UseEmergencyStop = false;
            options.UseAirPressureInterlock = false;
            foreach (var input in new[] { InputIo.EmergencyStop1Pressed, InputIo.AirPressureLow })
            {
                io.SetInput(input, true);
                await row.ToggleCommand.ExecuteAsync(null);
                Assert.True(io.GetOutput(OutputIo.MachineLight)); // A blocked toggle made no write.
                io.SetInput(input, false);
            }

            io.SetConnected(false);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.MachineLight));
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task DiagnosticMainConveyorRunsUntilStopAutoOrWindowCancellation()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.Alarm == MachineAlarm.MotionUnavailable, TimeSpan.FromSeconds(2)));
            var row = new OutputControlRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MainConveyorRun], machine);

            foreach (var stop in new Action[]
            {
                () => row.StopOutputTestCommand.Execute(null), // STOP remains available while busy.
                machine.Stop,
                () => io.SetInput(InputIo.AutoMode, false),
                row.ToggleCommand.Cancel, // OutputWindow.ShutdownAsync uses this cancellation.
            })
            {
                io.SetInput(InputIo.AutoMode, true);
                Assert.False(row.ToggleCommand.IsRunning);
                var run = row.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(2)));
                Assert.True(state.IsRunning);
                Assert.False(io.GetOutput(OutputIo.MainConveyorReverse));
                Assert.True(io.GetOutput(OutputIo.MainConveyorNormalSpeed));
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
                Assert.True(row.StopOutputTestCommand.CanExecute(null));
                stop();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(state.IsRunning);
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            }
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task DiagnosticMainConveyorKeepsSafetyAndPathAdmissionAndStopsOnMaterial()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var settings = services.GetRequiredService<MachineSettings>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            var row = new OutputControlRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MainConveyorRun], machine);
            async Task AssertBlockedAsync(OutputBlockReason reason)
            {
                state.RequestDisplayRefresh();
                Assert.True(await VirtualTest.WaitUntilAsync(() => row.BlockReason == reason,
                    TimeSpan.FromSeconds(2)));
                await row.ToggleCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(state.IsRunning);
            }

            settings.Units.MainConveyor = false;
            await AssertBlockedAsync(OutputBlockReason.MainConveyorDisabled);
            settings.Units.MainConveyor = true;
            settings.Units.PcbPlacement = true;
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
            await AssertBlockedAsync(OutputBlockReason.PlacementNotRaised);
            settings.Units.PcbPlacement = false;
            io.SetInput(InputIo.PcbPlacementHandlerUp, true);

            var options = services.GetRequiredService<MachineOptions>();
            options.UseEmergencyStop = false;
            options.UseAirPressureInterlock = false;
            foreach (var input in new[] { InputIo.EmergencyStop1Pressed, InputIo.AirPressureLow,
                InputIo.PcbPlacementCarrierPresent })
            {
                io.SetInput(input, true);
                await AssertBlockedAsync(input switch
                {
                    InputIo.EmergencyStop1Pressed => OutputBlockReason.EmergencyStop,
                    InputIo.AirPressureLow => OutputBlockReason.AirPressureLow,
                    _ => OutputBlockReason.MainConveyorCarrierDetected,
                });
                io.SetInput(input, false);
            }
            io.SetInput(InputIo.AutoMode, false);
            await AssertBlockedAsync(OutputBlockReason.AutoMode);
            io.SetInput(InputIo.AutoMode, true);

            foreach (var input in new[] { InputIo.MainConveyorEntryCarrierDetected, InputIo.AirPressureLow })
            {
                var run = row.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(2)));
                io.SetInput(input, true);
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(state.IsRunning);
                io.SetInput(input, false);
            }
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task ConveyorDirectionAndSpeedToggleBothWaysOnlyWhileIdle()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            foreach (var output in new[] { OutputIo.MainConveyorReverse, OutputIo.MainConveyorNormalSpeed,
                OutputIo.NgConveyorReverse, OutputIo.NgConveyorNormalSpeed })
            {
                var row = new OutputControlRow(signals.Outputs[output], machine);
                io.SetOutput(output, false);
                state.RequestDisplayRefresh();
                Assert.True(await VirtualTest.WaitUntilAsync(() => row.ToggleCommand.CanExecute(null),
                    TimeSpan.FromSeconds(2)));
                await row.ToggleCommand.ExecuteAsync(null);
                Assert.True(io.GetOutput(output));
                await row.ToggleCommand.ExecuteAsync(null);
                Assert.False(io.GetOutput(output));

                // Direct invocation must not change direction or speed during another operation.
                using (services.GetRequiredService<OperationCancellation>().Link())
                {
                    await row.ToggleCommand.ExecuteAsync(null);
                    Assert.False(io.GetOutput(output));
                    io.SetOutput(output, true);
                    await row.ToggleCommand.ExecuteAsync(null);
                    Assert.True(io.GetOutput(output));
                }
                io.SetOutput(output, false);
            }
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(OutputIo.MainConveyorRun)]
    [InlineData(OutputIo.NgConveyorRun)]
    public async Task ManualAndOutputsShareConveyorControlAndCanStopEachOther(OutputIo output)
    {
        using var services = CreateServices();
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
            Assert.Equal(new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun },
                manual.Conveyors.Select(row => row.Io.Signal));
            var manualRow = manual.Conveyors.Single(row => row.Io.Signal == output);
            var outputRow = new OutputControlRow(signals.Outputs[output], machine);
            foreach (var (start, stop) in new[] { (manualRow, outputRow), (outputRow, manualRow) })
            {
                Assert.True(await VirtualTest.WaitUntilAsync(() => start.BlockReason == OutputBlockReason.None,
                    TimeSpan.FromSeconds(2)));
                Assert.Equal(start.BlockReason, stop.BlockReason);
                var run = start.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(output), TimeSpan.FromSeconds(2)));
                signals.RefreshOutputs();
                if (start == outputRow)
                {
                    manual.Deactivate(); // Leaving Manual must not cancel OUTPUTS' operation.
                    Assert.True(io.GetOutput(output));
                }
                Assert.True(stop.StopOutputTestCommand.CanExecute(null));
                stop.StopOutputTestCommand.Execute(null);
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(output));
                Assert.False(state.IsRunning);
            }
            var ownedRun = manualRow.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            manual.Deactivate();
            await ownedRun.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(output));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        }
        finally { await manual.ShutdownAsync(); await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task NgMotorRunDoesNotUseCarrierOrShuttlePositionAsAdmission()
    {
        using var services = CreateServices();
        services.GetRequiredService<MachineSettings>().Units.NgConveyor = true;
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            var row = new OutputControlRow(services.GetRequiredService<IoSignals>()
                .Outputs[OutputIo.NgConveyorRun], machine);
            var shuttle = io.GetOutput(OutputIo.NgShuttleDown);
            var stopper = io.GetOutput(OutputIo.NgConveyorStopperUp);
            foreach (var up in new[] { true, false })
            {
                io.SetInput(InputIo.NgShuttleUp, up);
                io.SetInput(InputIo.NgShuttleDown, !up);
                var run = row.ToggleCommand.ExecuteAsync(null);
                Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(OutputIo.NgConveyorRun),
                    TimeSpan.FromSeconds(2)));
                Assert.False(io.GetOutput(OutputIo.NgConveyorReverse));
                Assert.True(io.GetOutput(OutputIo.NgConveyorNormalSpeed));
                Assert.Equal(shuttle, io.GetOutput(OutputIo.NgShuttleDown));
                Assert.Equal(stopper, io.GetOutput(OutputIo.NgConveyorStopperUp));
                // First pass: carriers appear while running. Second pass: start
                // with every carrier sensor already ON. Neither starts a sequence.
                var previous = state.Display;
                io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
                io.SetInput(InputIo.NgShuttleCarrierDetected, true);
                state.RequestDisplayRefresh();
                Assert.True(await VirtualTest.WaitUntilAsync(() => !ReferenceEquals(previous, state.Display),
                    TimeSpan.FromSeconds(2)));
                Assert.True(io.GetOutput(OutputIo.NgConveyorRun));
                Assert.False(run.IsCompleted);
                row.ToggleCommand.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            }
            var active = row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.NgConveyorRun));
            // Removing occupancy admission does not remove the MANUAL-only gate.
            io.SetInput(InputIo.AutoMode, false);
            await active.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(await VirtualTest.WaitUntilAsync(() => row.BlockReason == OutputBlockReason.AutoMode,
                TimeSpan.FromSeconds(2)));
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(state.IsRunning);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task ConveyorOutputReportsNgClearanceReasonInUiAndLog()
    {
        using var services = CreateServices();
        var settings = services.GetRequiredService<MachineSettings>();
        settings.Units.Inspection = true; // Inspection also shares the NG pickup clearance.
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            var row = new OutputControlRow(services.GetRequiredService<IoSignals>()
                .Outputs[OutputIo.MainConveyorRun], machine);
            async Task AssertReasonAsync(OutputBlockReason reason)
            {
                Assert.True(await VirtualTest.WaitUntilAsync(() => row.BlockReason == reason,
                    TimeSpan.FromSeconds(2)));
                Assert.Equal(reason, state.Display.MainConveyorPathBlock);
                Assert.Equal(reason == OutputBlockReason.None, row.ToggleCommand.CanExecute(null));
            }

            io.SetInput(InputIo.NgCarrierPickupUp, false);
            io.SetInput(InputIo.NgCarrierPickupDown, true);
            await AssertReasonAsync(OutputBlockReason.NgPickupNotRaised);
            Assert.Contains("[NgPickupNotRaised]", row.ToggleHint);
            var since = log.LatestSequence;
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Contains(log.ReadAfter(since), entry => entry.Message.Contains("ignored: [NgPickupNotRaised]"));

            io.SetInput(InputIo.NgCarrierPickupDown, false); // Neither limit is not proof of UP.
            await AssertReasonAsync(OutputBlockReason.NgPickupNotRaised);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            io.SetInput(InputIo.NgCarrierDetected, true);
            await AssertReasonAsync(OutputBlockReason.NgCarrierDetected);
            io.SetInput(InputIo.NgCarrierDetected, false);
            await AssertReasonAsync(OutputBlockReason.None);

            since = log.LatestSequence;
            var run = row.ToggleCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(() => io.GetOutput(OutputIo.MainConveyorRun),
                TimeSpan.FromSeconds(2)));
            Assert.True(row.StopOutputTestCommand.CanExecute(null)); // Busy must not disable OFF.
            io.SetInput(InputIo.NgCarrierPickupDown, true); // Conflicting UP/DOWN also blocks.
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Contains(log.ReadAfter(since), entry => entry.Message.Contains("stopped: [NgPickupNotRaised]"));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

            settings.Units.Inspection = false;
            state.RequestDisplayRefresh();
            await AssertReasonAsync(OutputBlockReason.None); // Disabled units do not add clearance gates.
            settings.Units.NgCarrierTransfer = true;
            state.RequestDisplayRefresh();
            await AssertReasonAsync(OutputBlockReason.NgPickupNotRaised);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(MachineAlarm.EmergencyStop)]
    [InlineData(MachineAlarm.DoorOpen)]
    [InlineData(MachineAlarm.AirPressureLow)]
    [InlineData(MachineAlarm.IoCommunication)]
    [InlineData(MachineAlarm.BufferConflict)]
    public async Task DiagnosticOutputsKeepSafetyAndCommunicationAlarmsBlocked(MachineAlarm alarm)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            SetAlarm(state, alarm);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.Alarm == alarm, TimeSpan.FromSeconds(2)));
            var row = new OutputControlRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MachineLight], machine);
            Assert.False(row.ToggleCommand.CanExecute(null));
            Assert.Contains("Read only", row.ToggleHint);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.MachineLight));
            Assert.Equal(alarm, state.Alarm);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task ModeSelectorUsesManualOnAndAutoOffWithoutChangingRawIo()
    {
        using var services = CreateServices();
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
            Assert.True(view.CanEditSettings);

            io.SetInput(InputIo.AutoMode, false);
            Assert.False(input.IsOn);
            Assert.True(state.AutoMode);
            Assert.False(state.ManualMode);
            Assert.False(view.CanEditSettings);
            Assert.False(state.ManualControlsEnabled);
            Assert.False(state.AutomaticRunning);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.AutoMode, TimeSpan.FromSeconds(2)));

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(input.IsOn);
            Assert.False(state.AutoMode);
            Assert.True(state.ManualMode);
            Assert.True(view.CanEditSettings);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !state.Display.AutoMode, TimeSpan.FromSeconds(2)));
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task AlarmSettingsRequireManualModeAndSavingDoesNotClearTheAlarm()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.SetInput(InputIo.AutoMode, false);
            Assert.False(view.CanEditSettings);
            Assert.Contains("MANUAL", view.SettingsAccessMessage);
            SetAlarm(state, MachineAlarm.Inspection);
            Assert.False(state.IsRunning);
            Assert.True(state.AutoMode);
            Assert.False(view.CanEditSettings);
            Assert.False(view.SaveSettingsCommand.CanExecute(null));
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(view.CanEditSettings);
            Assert.True(view.SaveSettingsCommand.CanExecute(null));
            Assert.Contains("without resetting", view.SettingsAccessMessage);
            Assert.False(state.ManualControlsEnabled);
            Assert.False(state.ManualSetupEnabled);
            Assert.False(machine.CanStart);
            Assert.False(machine.CanHome);

            view.Settings.AlphaMotion.ControllerNumber = 3;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.Equal(3, services.GetRequiredService<MachineStore>().LoadSettings()
                .Get<AlphaMotionSettings>().ControllerNumber);
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.False(io.GetInput(InputIo.ResetButton));
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task SettingsStayLockedWhileBusyOrClosingEvenWithAnAlarm()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<SettingsViewModel>();
        await machine.InitializeAsync();
        SetAlarm(state, MachineAlarm.Inspection);
        try
        {
            using (services.GetRequiredService<OperationCancellation>().Link())
            {
                Assert.False(view.CanEditSettings);
                Assert.False(view.SaveSettingsCommand.CanExecute(null));
                Assert.Contains("busy", view.SettingsAccessMessage);
                // Direct command execution also rechecks the guard, not just the button.
                await view.SaveSettingsCommand.ExecuteAsync(null);
                Assert.False(services.GetRequiredService<MachineStore>().HasData);
            }
            Assert.True(view.CanEditSettings);
        }
        finally { await machine.ShutdownAsync(); }
        Assert.False(view.CanEditSettings);
        Assert.False(view.SaveSettingsCommand.CanExecute(null));
        Assert.Contains("closing", view.SettingsAccessMessage);
    }

    [Fact]
    public async Task SoftwareResetWorksWithoutPhysicalResetInputAndKeepsAutoSettingsLocked()
    {
        using var services = CreateServices();
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
            Assert.False(view.CanEditSettings);
            Assert.True(machine.CanReset);
            await machine.ResetAsync();
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(view.CanEditSettings);
            Assert.False(io.GetInput(InputIo.ResetButton));
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsHoming);
            Assert.False(state.IsRunning);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(InputIo.EmergencyStop1Pressed)]
    [InlineData(InputIo.Door1Open)]
    [InlineData(InputIo.Door2Open)]
    [InlineData(InputIo.Door3Open)]
    [InlineData(InputIo.Door4Open)]
    [InlineData(InputIo.Door5Open)]
    [InlineData(InputIo.Door6Open)]
    [InlineData(InputIo.AirPressureLow)]
    public async Task SoftwareResetCannotClearAnActiveAutoSafetyFault(InputIo input)
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            var isDoor = input is InputIo.Door1Open or InputIo.Door2Open or InputIo.Door3Open
                or InputIo.Door4Open or InputIo.Door5Open or InputIo.Door6Open;
            Assert.True(state.DoorClosed);
            io.SetInput(InputIo.AutoMode, false);
            io.SetInput(input, !isDoor);
            var alarm = state.Alarm;
            Assert.NotEqual(MachineAlarm.None, alarm);
            Assert.False(machine.CanReset);
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
                Assert.False(machine.CanStart);
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => state.Display.Available && !state.Display.DoorClosed, TimeSpan.FromSeconds(2)));

                io.SetInput(input, true);
                Assert.True(signal.IsOn);
                Assert.True(state.DoorClosed);
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => state.Display.DoorClosed, TimeSpan.FromSeconds(2)));
                // Closing the door does not clear the latched alarm or restart the machine.
                Assert.Equal(alarm, state.Alarm);
                Assert.False(state.AutomaticRunning);
            }
        }
        finally { await machine.ShutdownAsync(); }
    }

    private static void SetAlarm(MachineState state, MachineAlarm alarm) =>
        typeof(MachineState).GetMethod("SetError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(state, [alarm, new IOException("Simulated commissioning alarm.")]);

    private static ServiceProvider CreateServices() => new ServiceCollection()
        .AddSingleton(new MachineStore(Path.Combine(Path.GetTempPath(), $"IBTM-alarm-recovery-{Guid.NewGuid():N}.db")))
        .AddIbtmApplication(new MachineSettings
        {
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual, Light = LightDriver.Virtual },
            Units = new()
            {
                MainConveyor = true, PcbSupply = false, PcbPlacement = false,
                PickupBoltFeeder = false, ShootingBoltFeeder = false, BoltFastening = false,
                Inspection = false, NgCarrierTransfer = false, NgShuttle = false, NgConveyor = false,
            },
        })
        .BuildServiceProvider();
}
