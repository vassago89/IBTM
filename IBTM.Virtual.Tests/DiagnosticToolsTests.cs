using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
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
    public void MotionMonitorUsesMappedNumbersAndKeepsDisabledAxesReadOnly()
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
        Assert.Equal("Unavailable · Disabled", row.Condition);
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
    public async Task MotionMonitorShowsFeedbackWithAxisAlarmServoOffAndLatchedMachineAlarm()
    {
        using var services = CreateServices(new RecordingLight());
        var settings = services.GetRequiredService<MachineSettings>();
        settings.Units.NgCarrierTransfer = true;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
        await machine.InitializeAsync();
        try
        {
            motion.SetAlarm(MotionAxis.X, true);
            motion.SetServo(MotionAxis.Y, false);
            typeof(MachineState).GetMethod("SetError",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(state, [MachineAlarm.MotionUnavailable, new IOException("Axis alarm is latched.")]);
            state.RequestDisplayRefresh();
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.Available && state.Display.MotionFaulted && !state.Display.ServoPowerOn,
                TimeSpan.FromSeconds(2)));
            var view = new MotionWindowViewModel(machine, state, settings);
            var axes = view.Axes.Where(row => row.Group == MotionGroup.InspectionGantry).ToArray();
            Assert.All(axes, row =>
            {
                Assert.NotNull(row.Feedback);
                Assert.NotEqual("—", row.Position);
                Assert.False(view.HomeAxisCommand.CanExecute(row));
            });
            var x = Assert.Single(axes, row => row.Axis == MotionAxis.X);
            var y = Assert.Single(axes, row => row.Axis == MotionAxis.Y);
            Assert.True(await VirtualTest.WaitUntilAsync(() => x.Alarm == true && y.ServoOn == false,
                TimeSpan.FromSeconds(2)));
            Assert.Equal("Alarm", x.Condition);
            Assert.True(x.Alarm);
            Assert.Equal("Servo Off", y.Condition);
            Assert.False(y.ServoOn);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        }
        finally { await machine.ShutdownAsync(); }
    }

    [Fact]
    public async Task MotionDiagnosticsKeepPollingDisabledAxesWithoutWindowOrEventsAndDespiteControlReadFailure()
    {
        var probe = System.Reflection.DispatchProxy.Create<IXyMotion, DiagnosticMotionProbe>();
        var diagnostics = (DiagnosticMotionProbe)probe;
        using var services = CreateServices(new RecordingLight(), collection =>
            collection.AddSingleton(provider => new IBTM.Inspection.InspectionGantry(probe,
                provider.GetRequiredService<IBTM.Inspection.NgCarrierTransfer>(),
                provider.GetRequiredService<OperationCancellation>(),
                provider.GetRequiredService<IBTM.Inspection.InspectionGantrySettings>())));
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var settings = services.GetRequiredService<MachineSettings>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            var view = new MotionWindowViewModel(machine, state, settings) { EnabledOnly = false };
            var x = Assert.Single(view.Axes, row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            var y = Assert.Single(view.Axes, row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.Y);
            Assert.False(x.Enabled);
            Assert.NotNull(x.Feedback);
            Assert.False(view.ToggleServoCommand.CanExecute(x));
            Assert.False(view.HomeAxisCommand.CanExecute(x));
            var reads = diagnostics.Reads;

            // Change raw state silently: no motion, input event, refresh request or monitor window.
            diagnostics.Position = 42;
            diagnostics.Alarmed = true;
            Assert.True(await VirtualTest.WaitUntilAsync(() => diagnostics.Reads > reads && x.Position == "42.000"
                && x.Alarm == true, TimeSpan.FromSeconds(2)));
            Assert.Equal(MachineAlarm.None, state.Alarm); // Disabled axes are diagnostic only.
            Assert.False(state.Display.MotionFaulted);

            diagnostics.FailX = true;
            diagnostics.Position = 43;
            Assert.True(await VirtualTest.WaitUntilAsync(() => x.Feedback is null && x.Position == "43.000"
                && y.Feedback is not null, TimeSpan.FromSeconds(2)));
            Assert.NotNull(x.ReadError);
            Assert.True(state.Display.Available);

            // A failed enabled control scan must not hide the independent monitor cache
            // or throw while WPF evaluates the RESET button.
            settings.Units.NgCarrierTransfer = true;
            diagnostics.FailControl = true;
            Assert.True(await VirtualTest.WaitUntilAsync(() => !state.Display.Available,
                TimeSpan.FromSeconds(2)));
            Assert.NotNull(y.Feedback);
            Assert.NotEqual("—", y.Position);
            Assert.True(machine.CanReset);
            Assert.False(view.ToggleServoCommand.CanExecute(y));
            diagnostics.FailControl = false;
            settings.Units.NgCarrierTransfer = false;

            // Control-I/O loss does not stop independent motion diagnostics or allow control.
            io.SetConnected(false);
            diagnostics.FailX = false;
            diagnostics.Alarmed = false;
            diagnostics.Position = 44;
            Assert.True(await VirtualTest.WaitUntilAsync(() => x.Position == "44.000" && x.Alarm == false,
                TimeSpan.FromSeconds(2)));
            Assert.False(view.ToggleServoCommand.CanExecute(x));
            Assert.False(view.HomeAxisCommand.CanExecute(x));

            await machine.ShutdownAsync();
            Assert.True(services.GetRequiredService<IBTM.Inspection.InspectionGantry>()
                .Motion.MonitoringCompletion.IsCompletedSuccessfully);
        }
        finally { await machine.ShutdownAsync(); }
    }

    public class DiagnosticMotionProbe : System.Reflection.DispatchProxy, IMotionDiagnostics
    {
        private readonly VirtualMotionService _motion = new(new(), new(), hasZ: false);
        public volatile bool Alarmed;
        public volatile bool FailX;
        public volatile bool FailControl;
        public int Position;
        public int Reads;
        public AxisState ReadDiagnosticState(MotionAxis axis)
        {
            Interlocked.Increment(ref Reads);
            if (FailX && axis == MotionAxis.X) throw new IOException("Diagnostic X read failed.");
            return new(false, false, Alarmed, true, false, true, false, false);
        }
        public double ReadDiagnosticPosition(MotionAxis axis) => Volatile.Read(ref Position);
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments)
        {
            if (FailControl && method!.Name == nameof(IMotionFeedback.GetAxisState))
                throw new IOException("Control feedback read failed.");
            return method!.Invoke(_motion, arguments);
        }
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
    public async Task InterfaceOutputStaysOnUntilOffOrPeerInterlock()
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
            Assert.Equal("ON", row.ToggleLabel);
            var test = row.ToggleCommand.ExecuteAsync(null);
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorReadyToFront2), TimeSpan.FromSeconds(2)));
            Assert.True(state.IsRunning);
            await Task.Delay(1100);
            Assert.True(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(test.IsCompleted);
            Assert.Equal("OFF", row.ToggleLabel);
            Assert.True(row.ActionCommand.CanExecute(null));
            row.ActionCommand.Execute(null);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(state.IsRunning);
            Assert.Equal("ON", row.ToggleLabel);

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

    [Fact]
    public async Task InterfaceOutputSendsOffWhileUiContextIsBlocked()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var context = new PausedSynchronizationContext();
        OutputControlRow? row = null;
        Task? test = null;
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            row = new OutputControlRow(io, services.GetRequiredService<IoSignals>()
                .Outputs[OutputIo.MainConveyorReadyToFront2], machine);
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                test = row.ToggleCommand.ExecuteAsync(null);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            Assert.True(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            row.ActionCommand.Execute(null);
            // No queued UI callback is allowed to run before OFF is observed.
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => !io.GetOutput(OutputIo.MainConveyorReadyToFront2), TimeSpan.FromSeconds(2)));
            Assert.False(test.IsCompleted); // Only the UI command completion is still queued.
        }
        finally
        {
            row?.ToggleCommand.Cancel();
            context.Release();
            if (test is not null) await test.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    private sealed class PausedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Action> _pending = new();
        private int _released;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _pending.Enqueue(() => callback(state));
            if (Volatile.Read(ref _released) != 0) Drain();
        }
        public void Release()
        {
            Volatile.Write(ref _released, 1);
            Drain();
        }
        private void Drain()
        {
            while (_pending.TryDequeue(out var callback))
                ThreadPool.QueueUserWorkItem(_ => callback());
        }
    }

    private static ServiceProvider CreateServices(RecordingLight light, Action<ServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection();
        collection
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
        .AddSingleton<ILightController>(light);
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

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
