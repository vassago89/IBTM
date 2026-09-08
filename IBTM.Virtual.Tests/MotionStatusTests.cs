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
        var first = new ManualAxisRow(MotionGroup.PcbSupply, default, MotionAxis.X, status);
        var second = new ManualAxisRow(MotionGroup.PcbSupply, default, MotionAxis.X, status);

        Assert.Same(first.Feedback, second.Feedback);
        Assert.Equal(AxisCondition.Unavailable, first.Feedback.Condition);
        motion.Publish();
        Assert.Equal(0, motion.Reads);

        status.RefreshAxes();
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
        status.RefreshAxes();
        Assert.Equal(AxisCondition.ServoOff, first.Feedback.Condition);
        Assert.Equal(first.Feedback.Condition, second.Feedback.Condition);
    }

    private sealed class StatusMotion() : AjinMotionService(
        new AjinController(new AjinSettings()), new AxisHardware(), null, null,
        0.01, new MotionSettings(), new MachineOptions(), new OperationCancellation(), null)
    {
        public AxisState State = new(true, true, false, true, false, false, false, false);
        public Exception? Failure;
        public int Reads;
        public override bool IsReady => true;
        public override AxisState GetAxisState(MotionAxis axis)
        {
            Reads++;
            return Failure is { } failure ? throw failure : State;
        }
        public void Publish() => PublishStateChanged();
    }
}
