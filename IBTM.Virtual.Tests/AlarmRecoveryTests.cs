using System;
using System.IO;
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
            var row = new OutputControlRow(io, signals.Outputs[OutputIo.MachineLight], machine);
            Assert.True(row.ToggleCommand.CanExecute(null));
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.MachineLight));
            Assert.Equal(alarm, state.Alarm);
            Assert.False(state.ManualControlsEnabled);
            Assert.False(state.ManualOutputsEnabled); // Teaching/automatic admission is unchanged.

            foreach (var output in new[] { OutputIo.MainConveyorRun, OutputIo.ShootBolt, OutputIo.NgShuttleDown })
            {
                var restricted = new OutputControlRow(io, signals.Outputs[output], machine);
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
            var row = new OutputControlRow(io,
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
            Assert.False(state.ManualOutputsEnabled);
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
            Assert.False(state.ManualOutputsEnabled);

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
