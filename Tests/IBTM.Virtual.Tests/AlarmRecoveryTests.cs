using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.AlphaMotion;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AlarmRecoveryTests
{
    [Fact]
    public async Task TowerLampsAndBuzzerFollowMachineStateNotNgMotorStop()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var ngConveyor = services.GetRequiredService<IBTM.NgConveyor.NgCarrierConveyor>();
        await machine.InitializeAsync();
        try
        {
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            io.SetInput(InputIo.AutoMode, false); // Selecting AUTO alone is not Auto Run.
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.AutoMode,
                TimeSpan.FromSeconds(2)));
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
            state.SetAutomaticRunning(true);
            await AssertIndicatorsAsync(OutputIo.TowerLampGreen, false);

            state.SetError(MachineAlarm.MotionUnavailable);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            ngConveyor.Stop();
            Assert.True(io.GetOutput(OutputIo.Buzzer));
            state.ClearError();
            await AssertIndicatorsAsync(OutputIo.TowerLampGreen, false);

            io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
            io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            Assert.True(ngConveyor.Full);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            state.SetAutomaticRunning(false);
            await AssertIndicatorsAsync(OutputIo.TowerLampRed, true);
            io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            await AssertIndicatorsAsync(OutputIo.TowerLampYellow, false);
        }
        finally
        {
            state.SetAutomaticRunning(false);
            await machine.ShutdownAsync();
        }

        async Task AssertIndicatorsAsync(OutputIo lamp, bool buzzer)
        {
            OutputIo[] lamps = [OutputIo.TowerLampGreen, OutputIo.TowerLampYellow, OutputIo.TowerLampRed];
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => lamps.All(output => io.GetOutput(output) == (output == lamp))
                    && io.GetOutput(OutputIo.Buzzer) == buzzer,
                TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task DirectOutputsChangeOnlyTheSelectedSignalWithoutSetupOrFeedbackAdmission()
    {
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        // Test the direct write itself; common indicators have their own state policy.
        await state.StopDisplayUpdatesAsync();
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

            foreach (var output in Enum.GetValues<OutputIo>())
            {
                io.SetOutput(output, false);
                writes.Clear();
                var row = new OutputWindowRow(signals.Outputs[output], machine);
                row.ToggleCommand.Execute(null);
                Assert.True(io.GetOutput(output));
                // No feedback wait, no CanExecute gate, no display refresh needed for OFF.
                Assert.True(row.ToggleCommand.CanExecute(null));
                row.ToggleCommand.Execute(null);
                Assert.False(io.GetOutput(output));
                Assert.Null(row.ActionMessage);
                Assert.Equal(new[] { (output, true), (output, false) }, writes);
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
        using var services = CreateServices();
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
            }.Select(output => new OutputWindowRow(signals.Outputs[output], machine))
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
                    Assert.Contains("AutoMode", row.ActionMessage);
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
                    Assert.Contains("EmergencyStop", row.ActionMessage);
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
        using var services = CreateServices();
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
                row.StopCommand.Execute(null);
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
        using var services = CreateServices();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            SetAlarm(state, MachineAlarm.MotionUnavailable);
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => state.Display.Alarm == MachineAlarm.MotionUnavailable,
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
                    await VirtualTest.WaitUntilAsync(
                        () => io.GetOutput(OutputIo.MainConveyorRun),
                        TimeSpan.FromSeconds(2)));
                Assert.True(state.IsRunning);
                Assert.False(io.GetOutput(OutputIo.MainConveyorReverse));
                Assert.True(io.GetOutput(OutputIo.MainConveyorNormalSpeed));
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
            Assert.Equal(
                new[] { OutputIo.MainConveyorRun, OutputIo.NgConveyorRun },
                manual.Conveyors.Select(row => row.Io.Signal));
            var manualRow = manual.Conveyors.Single(row => row.Io.Signal == output);
            var outputRow = new OutputWindowRow(signals.Outputs[output], machine);

            var run = manualRow.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            outputRow.ToggleCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(output));
            Assert.False(state.IsRunning);

            outputRow.ToggleCommand.Execute(null);
            manual.Deactivate(); // Manual does not own an output started by OUTPUTS.
            Assert.True(io.GetOutput(output));
            Assert.True(manualRow.StopCommand.CanExecute(null));
            manualRow.StopCommand.Execute(null);
            Assert.False(io.GetOutput(output));
            Assert.False(state.IsRunning);

            var ownedRun = manualRow.RunCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(output));
            manual.Deactivate();
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
        using var services = CreateServices();
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
                    await VirtualTest.WaitUntilAsync(
                        () => io.GetOutput(OutputIo.NgConveyorRun),
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
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => !ReferenceEquals(previous, state.Display),
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
            Assert.Contains("[AutoMode]", row.ActionMessage);
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
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => state.Display.AutoMode, TimeSpan.FromSeconds(2)));

            io.SetInput(InputIo.AutoMode, true);
            Assert.True(input.IsOn);
            Assert.False(state.AutoMode);
            Assert.True(state.ManualMode);
            Assert.True(view.CanEditSettings);
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => !state.Display.AutoMode, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
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
            Assert.True(state.ManualSetupEnabled);
            Assert.False(machine.CanStart);
            Assert.False(machine.CanHome);

            var output = view.OutputMappings.Single(
                row => row.Signal.Equals(OutputIo.PcbPlacementStopperUp)).Output!;
            var axis = view.AxisMappings.Single(
                row => row.Signal.Equals(MachineAxis.InspectionGantryX)).Axis!;
            Assert.Same(view.Settings.ConveyorHardware.Outputs[OutputIo.PcbPlacementStopperUp], output);
            Assert.Same(view.Settings.InspectionGantryHardware.Axes[MachineAxis.InspectionGantryX], axis);
            var runningOutput = services.GetRequiredService<IReadOnlyDictionary<OutputIo, OutputHardware>>()
                [OutputIo.PcbPlacementStopperUp];
            var originalOutput = (runningOutput.Number, runningOutput.OffNumber, runningOutput.Feedback!.OnInput);
            var runningMotion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
            var originalRange = runningMotion.GetRange(MotionAxis.X);
            output.Number = 80;
            output.OffNumber = 81;
            output.Feedback!.OnInput = InputIo.InspectionStopperUp;
            axis.Number = 12;
            axis.Minimum = -5;
            axis.Maximum = 150;

            view.Settings.AlphaMotion.ControllerNumber = 3;
            var motion = view.Settings.InspectionGantry.Motion;
            var speed = motion.HorizontalSpeed;
            motion.HorizontalSpeed = double.NaN;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.StartsWith("Settings not saved:", view.DatabaseMessage);
            Assert.False(services.GetRequiredService<MachineStore>().HasData);
            Assert.True(view.CanEditSettings);

            motion.HorizontalSpeed = speed;
            await view.SaveSettingsCommand.ExecuteAsync(null);
            Assert.StartsWith("Settings saved.", view.DatabaseMessage);
            Assert.Equal(
                3,
                services.GetRequiredService<MachineStore>()
                    .LoadSettings()
                    .Get<AlphaMotionSettings>()
                    .ControllerNumber);
            var saved = services.GetRequiredService<MachineStore>().LoadSettings();
            var savedOutput = saved.Get<ConveyorHardwareSettings>().Outputs[OutputIo.PcbPlacementStopperUp];
            var savedAxis = saved.Get<InspectionGantryHardwareSettings>().Axes[MachineAxis.InspectionGantryX];
            Assert.Equal((80, (int?)81, InputIo.InspectionStopperUp),
                (savedOutput.Number, savedOutput.OffNumber, savedOutput.Feedback!.OnInput));
            Assert.Equal((12, -5d, 150d), (savedAxis.Number, savedAxis.Minimum, savedAxis.Maximum));
            Assert.Equal(originalOutput,
                (runningOutput.Number, runningOutput.OffNumber, runningOutput.Feedback.OnInput));
            Assert.Equal(originalRange, runningMotion.GetRange(MotionAxis.X));
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.False(io.GetInput(InputIo.ResetButton));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
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
        finally
        {
            await machine.ShutdownAsync();
        }

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
        using var services = CreateServices();
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
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => state.Display.Available && !state.Display.DoorClosed,
                        TimeSpan.FromSeconds(2)));

                io.SetInput(input, true);
                Assert.True(signal.IsOn);
                Assert.True(state.DoorClosed);
                Assert.True(
                    await VirtualTest.WaitUntilAsync(
                        () => state.Display.DoorClosed,
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
            VirtualTest.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-alarm-recovery-{Guid.NewGuid():N}.db")))
            .AddIbtmApplication(
                new MachineSettings

                {

                    Drivers = new() { Inspection = InspectionAlgorithm.Virtual, Light = LightDriver.Virtual },
                    Units = new()
                    {
                        MainConveyor = true,
                        PcbSupply = false,
                        PcbPlacement = false,
                        PickupBoltFeeder = false,
                        ShootingBoltFeeder = false,
                        BoltFastening = false,
                        Inspection = false,
                        NgCarrierTransfer = false,
                        NgShuttle = false,
                        NgConveyor = false,
                    },
                })
            .BuildServiceProvider();
    }
}
