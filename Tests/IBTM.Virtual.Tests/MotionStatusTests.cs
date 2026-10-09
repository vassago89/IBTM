using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbSupply;
using IBTM.Tests;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionStatusTests
{
    [Fact]
    public async Task MotionPollingUsesTheCurrentIntervalWithoutRestarting()
    {
        var options = new MachineOptions { MotionPollMilliseconds = 1_000 };
        var hardware = new PcbSupplyHardwareSettings();
        var io = new VirtualIoService(hardware.Outputs, options);
        io.Initialize();
        var motions = new Dictionary<MotionGroup, MotionStatus>
        {
            [MotionGroup.PcbSupply] = new(new StatusMotion()),
        };
        await using var monitor = new MachineFeedbackMonitor(
            new UnitSettings(), options, io, new IoSignals([hardware], io), motions);
        var completed = new TaskCompletionSource<(TimeSpan Slow, TimeSpan Fast)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = 0;
        var previous = 0L;
        var slow = TimeSpan.Zero;
        monitor.Sampled += (group, sample) =>
        {
            samples++;
            if (samples == 2)
            {
                slow = Stopwatch.GetElapsedTime(previous, sample.StartedAt);
                options.MotionPollMilliseconds = 50;
            }
            else if (samples == 3)
            {
                completed.TrySetResult((slow, Stopwatch.GetElapsedTime(previous, sample.StartedAt)));
            }
            previous = sample.StartedAt;
        };

        await monitor.StartAsync();
        var intervals = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(intervals.Slow >= TimeSpan.FromMilliseconds(900), intervals.Slow.ToString());
        Assert.True(intervals.Fast < TimeSpan.FromMilliseconds(750), intervals.Fast.ToString());
    }

    [Fact]
    public void SupplyTeachingRotationRequiresCurrentHealthyStationaryFeedback()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        var target = new AxisPosition { X = 12 };
        var io = new VirtualIoService(VirtualTestSupport.Outputs(new PcbSupplyHardwareSettings()), new());
        io.Initialize();
        var supply = VirtualTestSupport.CreateSupplier(motion, io, new() { HandoffPosition = target });
        Assert.True(supply.IsTeachingRotationAllowed);

        motion.ReportedPosition = (15, 0, 0); // External encoder change, without an application move event.
        Assert.Equal(12, status.Position.X);
        Assert.False(supply.IsTeachingRotationAllowed);
        motion.ReportedPosition = (12, 0, 0);
        motion.State = motion.State with { ServoOn = false };
        Assert.True(status.Axes[MotionAxis.X].State?.ServoOn);
        Assert.False(supply.IsTeachingRotationAllowed);
        motion.State = motion.State with { ServoOn = true, Alarm = true };
        Assert.False(supply.IsTeachingRotationAllowed);
        motion.State = motion.State with { Alarm = false, InMotion = true };
        Assert.False(supply.IsTeachingRotationAllowed);
        motion.State = motion.State with { InMotion = false };
        Assert.True(supply.IsTeachingRotationAllowed);
        motion.Failure = new IOException("Current feedback is unavailable.");
        Assert.Throws<IOException>(() => supply.IsTeachingRotationAllowed);
    }

    [Fact]
    public async Task SupplyTeachingRotationRetainsRejectedFeedbackAfterItRecovers()
    {
        var motion = new StatusMotion();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(new PcbSupplyHardwareSettings()), new());
        io.Initialize();
        var supply = VirtualTestSupport.CreateSupplier(motion, io, new() { HandoffPosition = new() { X = 12 } });
        io.AutoResponseEnabled = false;
        var rotation = supply.SetTeachingRotationAsync(true);
        motion.State = motion.State with { InPosition = false };
        motion.Publish();
        motion.State = motion.State with { InPosition = true };
        motion.Publish();

        var error = await Assert.ThrowsAsync<MotionInterlockException>(() => rotation);

        Assert.Contains("X", error.Message);
        Assert.Contains("InPosition = False", error.Message);
        Assert.True(supply.IsTeachingRotationAllowed);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyRotate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MotionReadinessRequiresHomeFeedbackForEveryInstalledAxis(bool hasY)
    {
        using var motion = new VirtualMotionService(new(), new(), hasY: hasY, hasZ: false);
        var status = new MotionStatus(motion);
        motion.Initialize();
        Assert.False(MotionServiceBase.IsReadyAndStopped(motion));
        Assert.False(status.IsFeedbackAvailable);
        await motion.HomeAsync(MotionAxis.X, 1_000);
        if (hasY)
        {
            Assert.False(MotionServiceBase.IsReadyAndStopped(motion));
            await motion.HomeAsync(MotionAxis.Y, 1_000);
        }
        Assert.True(MotionServiceBase.IsReadyAndStopped(motion));
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.True(status.IsFeedbackAvailable);
        Assert.True(status.XyHomed);
        Assert.Equal(0, status.Position.X);
    }

    [Fact]
    public void DiagnosticErrorsKeepLatestExceptionAndReportOnceUntilFeedbackRecovers()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        var diagnostics = status.MonitorAxes[MotionAxis.X];
        var reports = 0;
        var changes = 0;
        diagnostics.PropertyChanged += (_, _) => changes++;
        void Report(MotionAxis axis, Exception error)
        {
            Assert.Equal(MotionAxis.X, axis);
            reports++;
        }

        motion.Failure = new IOException("Feedback failed.");
        status.RefreshMonitorFeedback(Report);
        Assert.Equal(1, reports);
        var previous = diagnostics.Sample;
        var previousChanges = changes;

        // Equal text does not make two failures the same: retain the new cause and stack.
        motion.Failure = new IOException("Feedback failed.", new InvalidOperationException());
        status.RefreshMonitorFeedback(Report);
        Assert.NotSame(previous, diagnostics.Sample);
        Assert.True(changes > previousChanges);
        var error = Assert.IsType<AggregateException>(diagnostics.Sample.ReadError);
        Assert.All(error.InnerExceptions, failure => Assert.Same(motion.Failure, failure));
        Assert.Equal(1, reports);

        motion.Failure = null;
        status.RefreshMonitorFeedback(Report);
        Assert.Null(diagnostics.Sample.ReadError);
        motion.Failure = new IOException("Feedback failed.");
        status.RefreshMonitorFeedback(Report);
        Assert.Equal(2, reports);
    }

    [Fact]
    public async Task FailedAdjustmentDoesNotReadFeedbackAgainToClearCommandHistory()
    {
        var motion = new StatusMotion();
        motion.State = motion.State with { InPosition = false };

        // A stopped axis may accept a command without the previous target being in position.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => motion.AdjustAxisAsync(MotionAxis.X, 0, 1));

        Assert.Equal("Move was rejected.", failure.Message);
        Assert.Equal(MotionCommand.None, motion.Command);
        Assert.Throws<IOException>(() => motion.GetAxisState(MotionAxis.X));
    }

    [Fact]
    public void DisplaysShareFeedbackAndDoNotReadTheDevice()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        var first = status.Axes[MotionAxis.X];
        var second = status.Axes[MotionAxis.X];

        Assert.Same(first, second);
        Assert.Equal(AxisCondition.Unavailable, first.Condition);
        Assert.False(status.IsFeedbackAvailable);
        Assert.Null(status.Position.X);
        motion.Publish();
        Assert.Equal(0, motion.Reads);

        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.Equal(new MotionPosition(12, null, null), status.Position);
        Assert.Equal(status.MonitorAxes[MotionAxis.X].Sample.Position, status.Position.X);
        Assert.Equal(AxisCondition.Ready, first.Condition);
        Assert.True(status.IsFeedbackAvailable);
        Assert.True(status.XyHomed);
        Assert.Equal(1, motion.Reads);
        Assert.Equal(1, motion.PositionReads); // Control refresh and bindings do not read coordinates again.
        status.RefreshControlFeedback();
        Assert.Equal(1, motion.Reads); // Nor do they acquire a second axis-state sample.

        motion.Failure = new IOException("Axis feedback unavailable.");
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        var reads = motion.Reads;
        Assert.Equal(AxisCondition.Unavailable, first.Condition);
        Assert.False(status.IsFeedbackAvailable);
        Assert.Null(second.State);
        Assert.False(status.XyHomed);
        Assert.Null(status.Position.X);
        Assert.Equal(reads, motion.Reads);

        motion.Failure = null;
        motion.State = motion.State with { Homed = false };
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.Equal(AxisCondition.HomeRequired, first.Condition);
        Assert.Equal(first.Condition, second.Condition);
        Assert.False(status.XyHomed);
        // External card state can change while no application move is active.
        motion.State = motion.State with { ServoOn = false };
        motion.ReportedPosition = (24, 0, 0); // No PositionChanged event from an external adjustment.
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.Equal(new MotionPosition(24, null, null), status.Position);
        Assert.Equal(AxisCondition.ServoOff, first.Condition);
        Assert.Equal(first.Condition, second.Condition);
    }

    [Fact]
    public void UnavailableControlInvalidatesReadyAxesWithoutReadingTheDriver()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.True(status.XyHomed);
        var reads = motion.Reads;
        var positionReads = motion.PositionReads;
        motion.Failure = new IOException("AXL connection closed.");
        motion.ReadinessFailure = motion.Failure;

        status.RefreshControlFeedback(available: false);

        Assert.Equal(reads, motion.Reads);
        Assert.Equal(positionReads, motion.PositionReads);
        Assert.Equal(12, status.Position.X); // Control availability does not replace readable coordinates.
        Assert.False(status.XyHomed);
        Assert.All(status.Axes.Values, axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Same(motion.Failure, Assert.Throws<IOException>(() => status.RefreshControlFeedback()));
        status.RefreshMonitorFeedback();
        Assert.Null(status.Position.X);
    }

    [Fact]
    public async Task StationsAndMonitorUseTheRegisteredMotionAndStatusInstances()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
        var statuses = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, MotionStatus>>();
        var monitor = services.GetRequiredService<MachineFeedbackMonitor>();
        Assert.Same(statuses, monitor.Motions);
        foreach (var (group, status) in statuses)
            Assert.Same(motions[group], status.Feedback);
        Assert.Same(statuses[MotionGroup.PcbSupply], services.GetRequiredService<IBTM.PcbSupply.PcbSupplier>().Motion);
        Assert.Same(statuses[MotionGroup.PcbPlacementHandler], services.GetRequiredService<IBTM.PcbPlacement.PcbPlacer>().Motion);
        Assert.Same(statuses[MotionGroup.BoltFastening], services.GetRequiredService<IBTM.BoltFastening.BoltFasteningStation>().Motion);
        Assert.Same(statuses[MotionGroup.InspectionGantry], services.GetRequiredService<InspectionStation>().Motion);
        Assert.Same(motions[MotionGroup.InspectionGantry], services.GetRequiredService<InspectionStation>().Motion.Feedback);
    }

    [Fact]
    public async Task MotionMonitorShowsFeedbackWithAxisAlarmServoOffAndLatchedMachineAlarm()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var settings = services.GetRequiredService<MachineSettings>();
        settings.Units.Inspection = true;
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
            state.Refresh();
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => state.Available
                        && state.FeedbackReadiness.Faulted
                        && !state.ServoPowerOn,
                    TimeSpan.FromSeconds(2)));
            var view = new MotionDiagnosticsViewModel(machine, state, settings);
            var axes = view.Axes.Where(row => row.Group == MotionGroup.InspectionGantry).ToArray();
            Assert.All(
                axes,
                row =>
                {
                    Assert.NotNull(row.Diagnostics.Sample.State);
                    Assert.NotNull(row.Diagnostics.Sample.Position);
                    Assert.False(row.IsHomeAllowed);
                });
            var x = Assert.Single(axes, row => row.Axis == MotionAxis.X);
            var y = Assert.Single(axes, row => row.Axis == MotionAxis.Y);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => x.Diagnostics.Sample.Faulted == true
                        && y.Diagnostics.Sample.State?.ServoOn == false,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(AxisCondition.Alarm, x.Diagnostics.Sample.Condition);
            Assert.True(x.Diagnostics.Sample.Faulted);
            Assert.Equal(AxisCondition.ServoOff, y.Diagnostics.Sample.Condition);
            Assert.False(y.Diagnostics.Sample.State?.ServoOn);
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
        await using var services = MachineTestSupport.CreateDiagnosticServices(
            configure: collection =>
                collection.AddKeyedSingleton<IXyMotion>(MotionGroup.InspectionGantry, probe));
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var settings = services.GetRequiredService<MachineSettings>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            var view = new MotionDiagnosticsViewModel(machine, state, settings) { EnabledOnly = false };
            var x = Assert.Single(
                view.Axes,
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
            var y = Assert.Single(
                view.Axes,
                row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.Y);
            var position = services.GetRequiredService<IBTM.Inspection.InspectionStation>().Motion;
            Assert.False(x.Enabled);
            Assert.NotNull(x.Diagnostics.Sample.State);
            Assert.False(x.IsToggleServoAllowed);
            Assert.False(x.IsHomeAllowed);
            var reads = diagnostics.Reads;
            // Change raw state silently: no motion, input event, refresh request or monitor window.
            diagnostics.Position = 42;
            diagnostics.Alarmed = true;
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => diagnostics.Reads > reads
                        && x.Diagnostics.Sample.Position == 42
                        && x.Diagnostics.Sample.Faulted == true,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(MachineAlarm.None, state.Alarm); // Disabled axes are diagnostic only.
            Assert.Equal(42, position.Position.X);
            Assert.False(state.FeedbackReadiness.Faulted);

            // Movement started outside the application still makes the machine busy.
            diagnostics.InMotion = true;
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => state.IsRunning, TimeSpan.FromSeconds(2)));
            Assert.False(machine.IsResetAllowed);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.True(state.SetupEditingEnabled);
            Assert.True(services.GetRequiredService<SettingsViewModel>().IsSettingsEditAllowed);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => state.IsRunning && state.SetupEditingEnabled,
                TimeSpan.FromSeconds(2)));
            Assert.False(state.SetupEnabled);

            await view.StopCommand.ExecuteAsync(null);
            Assert.Equal(1, diagnostics.Stops);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => !state.IsRunning, TimeSpan.FromSeconds(2)));

            using (services.GetRequiredService<OperationCancellation>().TryBegin())
            {
                Assert.False(state.SetupEditingEnabled);
                Assert.False(services.GetRequiredService<SettingsViewModel>().IsSettingsEditAllowed);
            }
            Assert.True(state.SetupEditingEnabled);

            diagnostics.FailX = true;
            diagnostics.Position = 43;
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => x.Diagnostics.Sample.State is null
                        && x.Diagnostics.Sample.Position == 43
                        && y.Diagnostics.Sample.State is not null,
                    TimeSpan.FromSeconds(2)));
            Assert.NotNull(x.Diagnostics.Sample.ReadError);
            Assert.Equal(43, position.Position.X); // A state-query failure does not hide a readable coordinate.
            Assert.True(state.Available);
            diagnostics.FailPosition = true;
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => position.Position.X is null && x.Diagnostics.Sample.Position is null,
                TimeSpan.FromSeconds(2)));
            var readErrors = Assert.IsType<AggregateException>(x.Diagnostics.Sample.ReadError);
            Assert.Collection(
                readErrors.InnerExceptions,
                error => Assert.Equal("Diagnostic X read failed.", error.Message),
                error => Assert.Equal("Diagnostic X position read failed.", error.Message));
            Assert.Equal(43, position.Position.Y);
            diagnostics.FailPosition = false;
            // A failed enabled control scan must not hide the independent monitor cache
            // or throw while WPF evaluates the RESET button.
            settings.Units.Inspection = true;
            Assert.True(x.Enabled);
            x.Refresh();
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => state.FeedbackReadiness.Faulted && state.ReadError is not null && !y.IsToggleServoAllowed,
                TimeSpan.FromSeconds(2)));
            Assert.NotNull(state.ReadError); // Explicit failure without another throwing control read.
            diagnostics.FailX = false;
            diagnostics.FailControl = true;
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => !state.Available, TimeSpan.FromSeconds(2)));
            Assert.NotNull(y.Diagnostics.Sample.State);
            Assert.NotNull(y.Diagnostics.Sample.Position);
            Assert.True(machine.IsResetAllowed);
            Assert.False(y.IsToggleServoAllowed);
            diagnostics.FailControl = false;
            settings.Units.Inspection = false;
            x.Refresh();
            Assert.False(x.Enabled);
            // Control-I/O loss does not stop independent motion diagnostics or allow control.
            io.IsReady = false;
            diagnostics.FailX = false;
            diagnostics.Alarmed = false;
            diagnostics.Position = 44;
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(
                    () => x.Diagnostics.Sample.Position == 44
                        && x.Diagnostics.Sample.Faulted == false,
                    TimeSpan.FromSeconds(2)));
            Assert.Equal(44, position.Position.X);
            Assert.False(x.IsToggleServoAllowed);
            Assert.False(x.IsHomeAllowed);

            io.IsReady = true;
            await machine.ShutdownAsync();
            Assert.True(services.GetRequiredService<MachineFeedbackMonitor>().Completion.IsCompletedSuccessfully);
        }
        finally
        {
            io.IsReady = true;
            await machine.ShutdownAsync();
        }
    }

    private sealed class StatusMotion : AjinMotionService, IMotionDiagnostics
    {
        public AxisState State;
        public Exception? Failure;
        public Exception? ReadinessFailure;
        public int Reads;
        public int PositionReads;
        public (double X, double Y, double Z) ReportedPosition { get; set; }

        public StatusMotion()
            : base(
                new AjinController(new AjinSettings()),
                new TestMotionHardware(new(), null, null),
                new MotionSettings(),
                new MachineOptions(),
                new OperationCancellation(),
                null)
        {
            State = new(true, true, false, true, false, false, false, false);
            ReportedPosition = (12, 0, 0);
        }

        public override bool IsReady => ReadinessFailure is { } failure ? throw failure : true;

        public override (double X, double Y, double Z) Position
        {
            get
            {
                PositionReads++;
                return Failure is { } failure ? throw failure : ReportedPosition;
            }
        }

        public override AxisState GetAxisState(MotionAxis axis)
        {
            Reads++;
            return Failure is { } failure ? throw failure : State;
        }

        (AxisState? State, Exception? Error) IMotionDiagnostics.ReadDiagnosticState(MotionAxis axis)
        {
            Reads++;
            return Failure is { } failure ? (null, failure) : (State, null);
        }

        (double? Position, Exception? Error) IMotionDiagnostics.ReadDiagnosticPosition(MotionAxis axis)
        {
            PositionReads++;
            return Failure is { } failure ? (null, failure) : (ReportedPosition.X, null);
        }

        protected override Task MoveAsync(
            MotionAxis axis,
            double position,
            double velocity,
            CancellationToken cancellationToken,
            double? accelerationSeconds = null,
            double? decelerationSeconds = null)
        {
            Failure = new IOException("Feedback also became unavailable.");
            throw new InvalidOperationException("Move was rejected.");
        }

        public void Publish()
        {
            PublishStateChanged();
        }
    }

    public class DiagnosticMotionProbe : System.Reflection.DispatchProxy, IMotionDiagnostics
    {
        private readonly VirtualMotionService _motion;
        public volatile bool Alarmed;
        public volatile bool FailX;
        public volatile bool FailPosition;
        public volatile bool FailControl;
        public volatile bool InMotion;
        public int Position;
        public int Reads;
        public int Stops;

        public DiagnosticMotionProbe()
        {
            _motion = new(new(), new(), hasZ: false);
        }

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
            if (method!.Name == nameof(IAxisMotion.Stop))
            {
                Interlocked.Increment(ref Stops);
                InMotion = false;
            }
            if (method!.Name == "get_IsMoving")
                throw new IOException("Command availability must use the independent monitor sample.");
            if (FailControl && method!.Name == "get_" + nameof(IMotionFeedback.IsReady))
                throw new IOException("Control readiness read failed.");
            return method!.Invoke(_motion, arguments);
        }
    }
}
