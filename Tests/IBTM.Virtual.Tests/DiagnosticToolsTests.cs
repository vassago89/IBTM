using System;
using System.Collections.Concurrent;
using System.ComponentModel;
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
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            Assert.True(state.IsRunning);
            Assert.False(settings.CanEditSettings);
            Assert.False(machine.CanStart);
            state.SetError(MachineAlarm.MotionUnavailable, new IOException("Unrelated motion alarm."));
            Assert.True(settings.LightTestOn);
            Assert.False(test.IsCompleted);
            await settings.OffTestLightCommand.ExecuteAsync(null);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(settings.LightTestOn);
            Assert.Null(settings.PendingLightOffChannel);
            Assert.False(state.IsRunning);
            Assert.Contains("level:2:43", light.Calls);
            Assert.Equal("off:2", light.Calls.Last());

            test = settings.TestLightCommand.ExecuteAsync(null);
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => settings.LightTestOn, TimeSpan.FromSeconds(2)));
            io.SetInput(InputIo.AutoMode, false);
            await test.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("off:2", light.Calls.Last());
            Assert.False(state.IsRunning);
            var calls = light.Calls.Count;
            await settings.TestLightCommand.ExecuteAsync(null); // Direct invocation cannot bypass AUTO.
            Assert.Equal(calls, light.Calls.Count);
        }
        finally
        {
            await settings.ShutdownAsync();
            await machine.ShutdownAsync();
        }
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
            var shutdownFailure = await Assert.ThrowsAsync<InvalidOperationException>(settings.ShutdownAsync);
            Assert.Contains("Simulated OFF failure.", shutdownFailure.Message);
            Assert.Equal(2, settings.PendingLightOffChannel);

            // Keep another command pending so shutdown must handle its failure before retrying OFF.
            var commandFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCommand = new ManualResetEventSlim();
            var commandFailure = new InvalidOperationException("Simulated command failure.");
            void FailImageCommand(object? sender, PropertyChangedEventArgs args)
            {
                if (args.PropertyName != nameof(settings.VirtualImageError) || settings.VirtualImageError is null)
                    return;
                commandFailed.SetResult();
                Assert.True(releaseCommand.Wait(TimeSpan.FromSeconds(2)));
                throw commandFailure;
            }

            settings.PropertyChanged += FailImageCommand;
            var load = Task.Run(() => settings.LoadVirtualImageCommand.ExecuteAsync(
                Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png")));
            try
            {
                await commandFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var shutdown = settings.ShutdownAsync();
                releaseCommand.Set();
                Assert.Same(commandFailure, await Assert.ThrowsAsync<InvalidOperationException>(() => load));
                var failures = (await Assert.ThrowsAsync<AggregateException>(() => shutdown)).Flatten().InnerExceptions;
                Assert.Equal(2, failures.Count);
                Assert.Contains(commandFailure, failures);
                Assert.Contains(failures, failure => failure.Message.Contains("Simulated OFF failure."));
                Assert.Equal("off:2", light.Calls.Last());
                Assert.Equal(2, settings.PendingLightOffChannel);
            }
            finally
            {
                releaseCommand.Set();
                settings.PropertyChanged -= FailImageCommand;
            }

            light.FailOff = false;
            await settings.OffTestLightCommand.ExecuteAsync(null);
            Assert.Equal("off:2", light.Calls.Last());
            Assert.Equal(onWrites, light.Calls.Count(call => call.StartsWith("on:")));
            Assert.Null(settings.PendingLightOffChannel);
        }
        finally
        {
            await settings.ShutdownAsync();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MotionMonitorShowsFeedbackWithAxisAlarmServoOffAndLatchedMachineAlarm()
    {
        using var services = CreateServices(new RecordingLight());
        var settings = services.GetRequiredService<MachineSettings>();
        settings.Units.NgCarrierTransfer = true;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.InspectionGantry);
        await machine.InitializeAsync();
        try
        {
            motion.SetAlarm(MotionAxis.X, true);
            motion.SetServo(MotionAxis.Y, false);
            state.SetError(MachineAlarm.MotionUnavailable, new IOException("Axis alarm is latched."));
            state.RequestDisplayRefresh();
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => state.Display.Available
                        && state.Display.MotionFaulted
                        && !state.Display.ServoPowerOn,
                    TimeSpan.FromSeconds(2)));
            var view = new MotionWindowViewModel(machine, state, settings);
            var axes = view.Axes.Where(row => row.Group == MotionGroup.InspectionGantry).ToArray();
            Assert.All(
                axes,
                row =>
                {
                    Assert.NotNull(row.Diagnostics.Snapshot.State);
                    Assert.NotNull(row.Diagnostics.Snapshot.Position);
                    Assert.False(view.HomeAxisCommand.CanExecute(row));
                });
            var x = Assert.Single(axes, row => row.Axis == MotionAxis.X);
            var y = Assert.Single(axes, row => row.Axis == MotionAxis.Y);
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => x.Diagnostics.Snapshot.Faulted == true
                        && y.Diagnostics.Snapshot.State?.ServoOn == false,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(AxisCondition.Alarm, x.Diagnostics.Snapshot.Condition);
            Assert.True(x.Diagnostics.Snapshot.Faulted);
            Assert.Equal(AxisCondition.ServoOff, y.Diagnostics.Snapshot.Condition);
            Assert.False(y.Diagnostics.Snapshot.State?.ServoOn);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MotionDiagnosticsKeepPollingDisabledAxesWithoutWindowOrEventsAndDespiteControlReadFailure()
    {
        var probe = System.Reflection.DispatchProxy.Create<IXyMotion, DiagnosticMotionProbe>();
        var diagnostics = (DiagnosticMotionProbe)probe;
        using var services = CreateServices(
            new RecordingLight(),
            collection =>
                collection.AddSingleton(
                    provider =>
                        new IBTM.Inspection.InspectionGantry(
                            probe,
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
            var x = Assert.Single(
                view.Axes,
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            var y = Assert.Single(
                view.Axes,
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.Y);
            var position = services.GetRequiredService<IBTM.Inspection.InspectionGantry>().Motion;
            Assert.False(x.Enabled);
            Assert.NotNull(x.Diagnostics.Snapshot.State);
            Assert.False(view.ToggleServoCommand.CanExecute(x));
            Assert.False(view.HomeAxisCommand.CanExecute(x));
            var reads = diagnostics.Reads;
            // Change raw state silently: no motion, input event, refresh request or monitor window.
            diagnostics.Position = 42;
            diagnostics.Alarmed = true;
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => diagnostics.Reads > reads
                        && x.Diagnostics.Snapshot.Position == 42
                        && x.Diagnostics.Snapshot.Faulted == true,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(MachineAlarm.None, state.Alarm); // Disabled axes are diagnostic only.
            Assert.Equal(42, position.Position.X);
            Assert.False(state.Display.MotionFaulted);

            // Movement started outside the application still makes the machine busy.
            diagnostics.InMotion = true;
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => state.IsRunning, TimeSpan.FromSeconds(2)));
            Assert.False(machine.CanReset);
            diagnostics.InMotion = false;
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => !state.IsRunning, TimeSpan.FromSeconds(2)));

            diagnostics.FailX = true;
            diagnostics.Position = 43;
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => x.Diagnostics.Snapshot.State is null
                        && x.Diagnostics.Snapshot.Position == 43
                        && y.Diagnostics.Snapshot.State is not null,
                    TimeSpan.FromSeconds(2)));
            Assert.NotNull(x.Diagnostics.Snapshot.ReadError);
            Assert.Equal(43, position.Position.X); // A state-query failure does not hide a readable coordinate.
            Assert.True(state.Display.Available);
            diagnostics.FailPosition = true;
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => position.Position.X is null && x.Diagnostics.Snapshot.Position is null,
                TimeSpan.FromSeconds(2)));
            var readErrors = Assert.IsType<AggregateException>(x.Diagnostics.Snapshot.ReadError);
            Assert.Collection(
                readErrors.InnerExceptions,
                error => Assert.Equal("Diagnostic X read failed.", error.Message),
                error => Assert.Equal("Diagnostic X position read failed.", error.Message));
            Assert.Equal(43, position.Position.Y);
            diagnostics.FailPosition = false;
            // A failed enabled control scan must not hide the independent monitor cache
            // or throw while WPF evaluates the RESET button.
            settings.Units.NgCarrierTransfer = true;
            Assert.True(x.Enabled);
            Assert.True(x.RefreshEnabled());
            Assert.False(x.RefreshEnabled());
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.MotionFaulted && !view.ToggleServoCommand.CanExecute(y),
                TimeSpan.FromSeconds(2)));
            Assert.NotNull(state.Display.ReadError); // Explicit failure without another throwing control read.
            diagnostics.FailX = false;
            diagnostics.FailControl = true;
            Assert.True(
                await VirtualTest.WaitUntilAsync(() => !state.Display.Available, TimeSpan.FromSeconds(2)));
            Assert.NotNull(y.Diagnostics.Snapshot.State);
            Assert.NotNull(y.Diagnostics.Snapshot.Position);
            Assert.True(machine.CanReset);
            Assert.False(view.ToggleServoCommand.CanExecute(y));
            diagnostics.FailControl = false;
            settings.Units.NgCarrierTransfer = false;
            Assert.True(x.RefreshEnabled());
            Assert.False(x.Enabled);
            // Control-I/O loss does not stop independent motion diagnostics or allow control.
            io.SetConnected(false);
            diagnostics.FailX = false;
            diagnostics.Alarmed = false;
            diagnostics.Position = 44;
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => x.Diagnostics.Snapshot.Position == 44
                        && x.Diagnostics.Snapshot.Faulted == false,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(44, position.Position.X);
            Assert.False(view.ToggleServoCommand.CanExecute(x));
            Assert.False(view.HomeAxisCommand.CanExecute(x));

            await machine.ShutdownAsync();
            Assert.True(
                services.GetRequiredService<IBTM.Inspection.InspectionGantry>()
                    .Motion.MonitoringCompletion.IsCompletedSuccessfully);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    public class DiagnosticMotionProbe : System.Reflection.DispatchProxy, IMotionDiagnostics
    {
        private readonly VirtualMotionService _motion = new(new(), new(), hasZ: false);
        public volatile bool Alarmed;
        public volatile bool FailX;
        public volatile bool FailPosition;
        public volatile bool FailControl;
        public volatile bool InMotion;
        public int Position;
        public int Reads;
        public (AxisState? State, Exception? Error) ReadDiagnosticState(MotionAxis axis)
        {
            Interlocked.Increment(ref Reads);
            if (FailX && axis == MotionAxis.X)
                return (null, new IOException("Diagnostic X read failed."));
            return (new(false, false, Alarmed, true, false, true, false, false, InMotion), null);
        }

        public (double? Position, Exception? Error) ReadDiagnosticPosition(MotionAxis axis)
        {
            if (FailPosition && axis == MotionAxis.X)
                return (null, new IOException("Diagnostic X position read failed."));
            return (Volatile.Read(ref Position), null);
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == "get_IsMoving")
                throw new IOException("Command availability must use the independent monitor snapshot.");
            if (FailControl && method!.Name == nameof(IMotionFeedback.GetAxisState))
                throw new IOException("Control feedback read failed.");
            return method!.Invoke(_motion, arguments);
        }
    }

    [Fact]
    public async Task DirectInterfaceOutputStaysOnUntilOffDespitePeerFeedback()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            var row = new OutputWindowRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MainConveyorReadyToFront2],
                machine);
            row.ToggleCommand.Execute(null);
            await Task.Delay(1100); // Guard against restoring the old one-second pulse.
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.True(io.GetOutput(row.Io.Signal));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            row.ToggleCommand.Execute(null);
            Assert.False(io.GetOutput(row.Io.Signal));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ManualConveyorSendsOffWhileUiContextIsBlocked()
    {
        using var services = CreateServices(new RecordingLight());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var context = new PausedSynchronizationContext();
        ManualConveyorRow? row = null;
        Task? test = null;
        await machine.InitializeAsync();
        try
        {
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            row = new ManualConveyorRow(
                services.GetRequiredService<IoSignals>().Outputs[OutputIo.MainConveyorRun],
                machine);
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                test = row.RunCommand.ExecuteAsync(null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            row.StopCommand.Execute(null);
            // No queued UI callback is allowed to run before OFF is observed.
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => !io.GetOutput(OutputIo.MainConveyorRun),
                    TimeSpan.FromSeconds(2)));
            Assert.False(test.IsCompleted); // Only the UI command completion is still queued.
        }
        finally
        {
            row?.RunCommand.Cancel();
            context.Release();
            if (test is not null)
                await test.WaitAsync(TimeSpan.FromSeconds(2));
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
            if (Volatile.Read(ref _released) != 0)
                Drain();
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

    private static ServiceProvider CreateServices(
        RecordingLight light,
        Action<ServiceCollection>? configure = null)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(
            VirtualTest.OpenMachineStore(
                Path.Combine(Path.GetTempPath(), $"IBTM-diagnostic-{Guid.NewGuid():N}.db")))
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
            .AddSingleton<ILightController>(light);
        configure?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    private sealed class RecordingLight : ILightController
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public bool FailOn { get; init; }
        public bool FailOff { get; set; }

        public void Initialize()
        {
            Calls.Enqueue("initialize");
        }

        public void SetLevel(int channel, int level)
        {
            Calls.Enqueue($"level:{channel}:{level}");
        }

        public void TurnOn(int channel)
        {
            Calls.Enqueue($"on:{channel}");
            if (FailOn)
                throw new IOException("Simulated ON failure.");
        }

        public void TurnOff(int channel)
        {
            Calls.Enqueue($"off:{channel}");
            if (FailOff)
                throw new IOException("Simulated OFF failure.");
        }

        public void TurnOffAll()
        {
            Calls.Enqueue("off:all");
        }
    }
}
