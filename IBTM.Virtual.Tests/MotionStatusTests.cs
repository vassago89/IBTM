using System;
using System.IO;
using IBTM.Ajin;
using IBTM.Core;
using IBTM.Device;
using IBTM.UI;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class MotionStatusTests
{
    [Fact]
    public void DisplaysShareFeedbackAndDoNotReadTheDevice()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        var first = new ManualAxisRow(MotionGroup.PcbSupply, MotionAxis.X, status);
        var second = new ManualAxisRow(MotionGroup.PcbSupply, MotionAxis.X, status);

        Assert.Same(first.Feedback, second.Feedback);
        Assert.Equal(AxisCondition.Unavailable, first.Feedback.Condition);
        motion.Publish();
        Assert.Equal(0, motion.Reads);

        status.RefreshAxes();
        Assert.Equal(new MotionPosition(12, 0, 0), status.Position);
        Assert.Equal(AxisCondition.Ready, first.Feedback.Condition);
        Assert.True(status.XyHomed);
        Assert.Equal(1, motion.Reads);

        motion.Failure = new IOException("Axis feedback unavailable.");
        Assert.Same(motion.Failure, Assert.Throws<IOException>(status.RefreshAxes));
        var reads = motion.Reads;
        Assert.Equal(AxisCondition.Unavailable, first.Feedback.Condition);
        Assert.Null(second.Feedback.State);
        Assert.False(status.XyHomed);
        Assert.Equal(reads, motion.Reads);

        motion.Failure = null;
        motion.State = motion.State with { Homed = false };
        status.RefreshAxes();
        Assert.Equal(AxisCondition.HomeRequired, first.Feedback.Condition);
        Assert.Equal(first.Feedback.Condition, second.Feedback.Condition);
        Assert.False(status.XyHomed);

        // External card state can change while no application move is active.
        motion.State = motion.State with { ServoOn = false };
        motion.Position = (24, 0, 0); // No PositionChanged event from an external adjustment.
        status.RefreshAxes();
        Assert.Equal(new MotionPosition(24, 0, 0), status.Position);
        Assert.Equal(AxisCondition.ServoOff, first.Feedback.Condition);
        Assert.Equal(first.Feedback.Condition, second.Feedback.Condition);
    }

    [Fact]
    public void UnavailableControlInvalidatesReadyAxesWithoutReadingTheDriver()
    {
        var motion = new StatusMotion();
        var status = new MotionStatus(motion);
        status.RefreshAxes();
        Assert.True(status.XyHomed);
        var reads = motion.Reads;
        var positionReads = motion.PositionReads;
        motion.Failure = new IOException("AXL connection closed.");

        status.RefreshAxes(available: false);

        Assert.Equal(reads, motion.Reads);
        Assert.Equal(positionReads, motion.PositionReads);
        Assert.False(status.XyHomed);
        Assert.All(status.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));
        Assert.Same(motion.Failure, Assert.Throws<IOException>(status.RefreshAxes));
    }

    private sealed class StatusMotion() : AjinMotionService(
        new AjinController(new AjinSettings()), new AxisHardware(), null, null,
        0.01, new MotionSettings(), new MachineOptions(), new OperationCancellation(), null)
    {
        public AxisState State = new(true, true, false, true, false, false, false, false);
        public Exception? Failure;
        public int Reads;
        public int PositionReads;
        public (double X, double Y, double Z) Position = (12, 0, 0);
        public override bool IsReady => true;
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
        public void Publish() => PublishStateChanged();
    }
}
