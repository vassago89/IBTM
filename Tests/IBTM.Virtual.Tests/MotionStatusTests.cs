using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionStatusTests
{
    [Fact]
    public void HoldingPositionRequiresCurrentHealthyStationaryFeedback()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        var target = new AxisPosition { X = 12 };
        Assert.True(MotionService.IsHoldingPosition(motion, target));

        motion.ReportedPosition = (15, 0, 0); // External encoder change, without an application move event.
        Assert.Equal(12, status.Position.X);
        Assert.False(MotionService.IsHoldingPosition(motion, target));
        motion.ReportedPosition = (12, 0, 0);
        motion.State = motion.State with { ServoOn = false };
        Assert.True(status.Axes[MotionAxis.X].State?.ServoOn);
        Assert.False(MotionService.IsHoldingPosition(motion, target));
        motion.State = motion.State with { ServoOn = true, Alarm = true };
        Assert.False(MotionService.IsHoldingPosition(motion, target));
        motion.State = motion.State with { Alarm = false, InMotion = true };
        Assert.False(MotionService.IsHoldingPosition(motion, target));
        motion.State = motion.State with { InMotion = false };
        Assert.True(MotionService.IsHoldingPosition(motion, target));
        motion.Failure = new IOException("Current feedback is unavailable.");
        Assert.Throws<IOException>(() => MotionService.IsHoldingPosition(motion, target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PositionChecksOnlyInstalledAxesAndRequiresTheirHomeFeedback(bool hasY)
    {
        using var motion = new VirtualMotionService(new(), new(), hasY: hasY, hasZ: false);
        var status = new MotionStatus(motion);
        motion.Initialize();
        var target = new AxisPosition { X = 0, Y = hasY ? 0 : 123, Z = 456 };
        Assert.False(MotionService.IsAt(motion, target));
        Assert.False(status.IsFeedbackAvailable);
        await motion.HomeAsync(MotionAxis.X, 1_000);
        if (hasY)
        {
            Assert.False(MotionService.IsAt(motion, target));
            await motion.HomeAsync(MotionAxis.Y, 1_000);
        }
        Assert.True(MotionService.IsAt(motion, target));
        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.True(status.IsFeedbackAvailable);
        Assert.True(status.XyHomed);
        Assert.Equal(0, status.Position.X);
        target.X = 10;
        Assert.False(MotionService.IsAt(motion, target));
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
        var previous = diagnostics.Snapshot;
        var previousChanges = changes;

        // Equal text does not make two failures the same: retain the new cause and stack.
        motion.Failure = new IOException("Feedback failed.", new InvalidOperationException());
        status.RefreshMonitorFeedback(Report);
        Assert.NotSame(previous, diagnostics.Snapshot);
        Assert.True(changes > previousChanges);
        var error = Assert.IsType<AggregateException>(diagnostics.Snapshot.ReadError);
        Assert.All(error.InnerExceptions, failure => Assert.Same(motion.Failure, failure));
        Assert.Equal(1, reports);

        motion.Failure = null;
        status.RefreshMonitorFeedback(Report);
        Assert.Null(diagnostics.Snapshot.ReadError);
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
        Assert.Equal(status.MonitorAxes[MotionAxis.X].Snapshot.Position, status.Position.X);
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
                new AxisHardware(),
                null,
                null,
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
            CancellationToken cancellationToken)
        {
            Failure = new IOException("Feedback also became unavailable.");
            throw new InvalidOperationException("Move was rejected.");
        }

        public void Publish()
        {
            PublishStateChanged();
        }
    }
}
