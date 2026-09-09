using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class DiagnosticToolsTests
{
    [Fact]
    public async Task LightTestOwnsOperationUntilOffAndStopsOnAuto()
    {
        var light = new RecordingLight();
        using var services = CreateServices(light);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var settings = services.GetRequiredService<SettingsViewModel>();
        await machine.InitializeAsync();
        try
        {
            settings.LightTestChannel = 2;
            settings.LightTestLevel = 43;
            var test = settings.TestLightCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            Assert.True(state.IsRunning);
            Assert.False(settings.CanEditSettings);
            Assert.False(machine.CanStart);
            await settings.OffTestLightCommand.ExecuteAsync(null);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(settings.LightTestOn);
            Assert.Null(settings.PendingLightOffChannel);
            Assert.False(state.IsRunning);
            Assert.Contains("level:2:43", light.Calls);
            Assert.Equal("off:2", light.Calls.Last());

            test = settings.TestLightCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            io.SetInput(InputIo.AutoMode, false);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("off:2", light.Calls.Last());
            Assert.False(state.IsRunning);
            var calls = light.Calls.Count;
            await settings.TestLightCommand.ExecuteAsync(null); // Direct invocation cannot bypass AUTO.
            Assert.Equal(calls, light.Calls.Count);
        }
        finally { await settings.ShutdownAsync(); await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task LightTestCleansUpPartialOnFailureAndReportsOffFailure()
    {
        var light = new RecordingLight { FailOn = true, FailOff = true };
        using var services = CreateServices(light);
        var machine = services.GetRequiredService<MachineController>();
        var settings = services.GetRequiredService<SettingsViewModel>();
        await machine.InitializeAsync();
        try
        {
            settings.LightTestLevel = 256;
            await settings.TestLightCommand.ExecuteAsync(null);
            Assert.Empty(light.Calls);
            settings.LightTestLevel = 80;
            await settings.TestLightCommand.ExecuteAsync(null);
            Assert.Contains("off:2", light.Calls);
            Assert.Contains("state is unknown", settings.LightTestMessage);
            Assert.False(services.GetRequiredService<MachineState>().IsRunning);
            Assert.False(settings.LightTestOn);
            Assert.Equal(2, settings.PendingLightOffChannel);
            Assert.True(settings.OffTestLightCommand.CanExecute(null));
            Assert.False(settings.TestLightCommand.CanExecute(null));
            var onWrites = light.Calls.Count(call => call.StartsWith("on:"));
            settings.LightTestChannel = 7;
            services.GetRequiredService<VirtualIoService>().SetInput(InputIo.AutoMode, false);
            light.FailOff = false;
            await settings.OffTestLightCommand.ExecuteAsync(null);
            Assert.Equal("off:2", light.Calls.Last());
            Assert.Equal(onWrites, light.Calls.Count(call => call.StartsWith("on:")));
            Assert.Null(settings.PendingLightOffChannel);
        }
        finally { await settings.ShutdownAsync(); await machine.ShutdownAsync(); }
    }

    [Fact]
    public void MotionMonitorUsesMappedNumbersAndMarksDisabledAxesUnavailable()
    {
        using var services = CreateServices(new RecordingLight());
        var settings = services.GetRequiredService<MachineSettings>();
        var hardware = settings.PcbSupplyHardware;
        var axis = hardware.AxisSignals.First();
        hardware.Axes[axis.Value].Number = 27;
        var view = new MotionWindowViewModel(services.GetRequiredService<MachineController>(),
            services.GetRequiredService<MachineState>(), settings);
        var row = Assert.Single(view.Axes, item => item.Group == hardware.Group && item.Axis == axis.Key);
        Assert.Equal("027", row.Address);
        Assert.Equal("Disabled", row.Condition);
        Assert.Equal("—", row.Position);
        Assert.Null(row.Alarm);
        Assert.False(view.ToggleServoCommand.CanExecute(row));
        Assert.False(view.HomeAxisCommand.CanExecute(row));
        Assert.Empty(view.View.Cast<MotionMonitorAxis>());
        view.EnabledOnly = false;
        view.Search = "027";
        Assert.Same(row, Assert.Single(view.View.Cast<MotionMonitorAxis>()));
        hardware.Axes[axis.Value].Number = 28; // Unsaved mapping is not the running driver map.
        Assert.Equal("027", row.Address);
    }

    [Fact]
    public async Task InputsBecomeUnknownOnDisconnectAndRefreshOnReconnect()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        await machine.InitializeAsync();
        try
        {
            var door = signals.Inputs[InputIo.Door1Open];
            Assert.True(door.IsOn);
            var changes = 0;
            door.PropertyChanged += (_, _) => changes++;
            io.SetConnected(false);
            Assert.Null(door.IsOn);
            Assert.False(signals.InputsAvailable);
            Assert.True(changes > 0);
            changes = 0;
            io.SetConnected(true);
            signals.RefreshInputs();
            Assert.True(door.IsOn);
            Assert.True(signals.InputsAvailable);
            Assert.True(changes > 0);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task StopperTestReportsTimeoutAndRejectsAnOccupiedStation()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            services.GetRequiredService<MachineOptions>().TimeoutMilliseconds = 30;
            var row = new OutputControlRow(io, services.GetRequiredService<IoSignals>()
                .Outputs[OutputIo.PcbPlacementStopperUp], machine);
            var log = services.GetRequiredService<ApplicationLog>();
            var since = log.LatestSequence;
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementStopperUp));
            Assert.Equal(OutputFeedbackState.Timeout, row.FeedbackState);
            Assert.Contains("PCB Placement Stopper Up", row.FeedbackError);
            Assert.Contains(log.ReadAfter(since), entry => entry.Level == "ERROR"
                && entry.Message.Contains("PcbPlacementStopperUp") && entry.Detail!.Contains("timeout"));
            Assert.False(state.IsRunning);

            io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementStopperUp)); // No OFF write at an occupied station.
            io.SetInput(InputIo.PcbPlacementCarrierPresent, false);
            io.SetInput(InputIo.AutoMode, false);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementStopperUp)); // AUTO cannot bypass admission.
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task InterfaceTestReturnsOffOnTimeoutAndOnPeerReady()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            var row = new OutputControlRow(io, services.GetRequiredService<IoSignals>()
                .Outputs[OutputIo.MainConveyorReadyToFront2], machine);
            Assert.Equal("Test 1s", row.ToggleLabel);
            var test = row.ToggleCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorReadyToFront2), TimeSpan.FromSeconds(2)));
            Assert.True(state.IsRunning);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));

            test = row.ToggleCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorReadyToFront2), TimeSpan.FromSeconds(2)));
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        }
        finally { await machine.ShutdownAsync(); }
    }

    private static ServiceProvider CreateServices(RecordingLight light) => new ServiceCollection()
        .AddSingleton(new MachineStore(Path.Combine(Path.GetTempPath(), $"IBTM-diagnostic-{Guid.NewGuid():N}.db")))
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
        .AddSingleton<ILightController>(light)
        .BuildServiceProvider();

    private sealed class RecordingLight : ILightController
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public bool FailOn { get; init; }
        public bool FailOff { get; set; }
        public void Initialize() => Calls.Enqueue("initialize");
        public void SetLevel(int channel, int level) => Calls.Enqueue($"level:{channel}:{level}");
        public void TurnOn(int channel)
        {
            Calls.Enqueue($"on:{channel}");
            if (FailOn) throw new IOException("Simulated ON failure.");
        }
        public void TurnOff(int channel)
        {
            Calls.Enqueue($"off:{channel}");
            if (FailOff) throw new IOException("Simulated OFF failure.");
        }
        public void TurnOffAll() => Calls.Enqueue("off:all");
    }
}
