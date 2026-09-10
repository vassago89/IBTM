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
    public async Task FailedAdjustmentDoesNotReadFeedbackAgainToClearCommandHistory()
    {
        var motion = new StatusMotion();

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
        Assert.Null(status.Position.X);
        motion.Publish();
        Assert.Equal(0, motion.Reads);

        status.RefreshMonitorFeedback();
        status.RefreshControlFeedback();
        Assert.Equal(new MotionPosition(12, null, null), status.Position);
        Assert.Equal(status.MonitorAxes[MotionAxis.X].Snapshot.Position, status.Position.X);
        Assert.Equal(AxisCondition.Ready, first.Condition);
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
        motion.Position = (24, 0, 0); // No PositionChanged event from an external adjustment.
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

    private sealed class StatusMotion() : AjinMotionService(
        new AjinController(new AjinSettings()),
        new AxisHardware(),
        null,
        null,
        0.01,
        new MotionSettings(),
        new MachineOptions(),
        new OperationCancellation(),
        null), IMotionDiagnostics
    {
        public AxisState State = new(true, true, false, true, false, false, false, false);
        public Exception? Failure;
        public Exception? ReadinessFailure;
        public int Reads;
        public int PositionReads;
        public (double X, double Y, double Z) Position = (12, 0, 0);
        public override bool IsReady
        {
            get
            {
                return ReadinessFailure is { } failure ? throw failure : true;
            }
        }

        public override (double X, double Y, double Z) GetPosition()
        {
            PositionReads++;
            return Failure is { } failure ? throw failure : Position;
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
            return Failure is { } failure ? (null, failure) : (Position.X, null);
        }

        protected override Task MoveAxisCoreAsync(
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
