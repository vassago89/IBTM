using System;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualDeviceTests
{
    [Fact]
    public void IoStoresInputsAndOutputs()
    {
        var io = new VirtualIoService();

        io.SetInput(3, true);
        io.SetOutput(7, true);

        Assert.True(io.GetInput(3));
        Assert.True(io.GetOutput(7));

        io.TurnOffAll();

        Assert.False(io.GetOutput(7));
    }

    [Fact]
    public async Task IoProvidesMachineSignalsAndActuatorFeedback()
    {
        var io = new VirtualIoService();
        io.Initialize();

        Assert.True(io.GetInput(PcbPlacementStation.ShuttlePresentInputChannel));
        Assert.True(io.GetInput(PcbPlacementStation.PcbAvailableInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.ShuttlePresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.ShuttlePresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.SmemaReadyInputChannel));

        io.SetOutput(PcbPlacementStation.LiftChannel, true);
        await io.WaitForInputAsync(PcbPlacementStation.LiftChannel, true);
        Assert.True(io.GetInput(PcbPlacementStation.LiftChannel));

        io.SetOutput(PcbPlacementStation.LiftChannel, false);
        await io.WaitForInputAsync(PcbPlacementStation.LiftChannel, false);
        Assert.False(io.GetInput(PcbPlacementStation.LiftChannel));
    }

    [Fact]
    public async Task IoWaitsForInputChanges()
    {
        var io = new VirtualIoService();
        var waiting = io.WaitForInputAsync(5, true);

        Assert.False(waiting.IsCompleted);

        io.SetInput(5, true);
        await waiting;
    }

    [Fact]
    public async Task MotionMovesAndJogsAllAxes()
    {
        using var motion = new VirtualMotionService();
        motion.Initialize();

        await motion.MoveToXYAsync(1, 2, 100);
        await motion.MoveToZAsync(3, 100);

        Assert.Equal((1, 2, 3), motion.GetPosition());

        motion.JogX(10);
        await Task.Delay(50);
        motion.Stop();

        Assert.True(motion.GetPosition().X > 1);
    }

    [Fact]
    public async Task CameraStreamsFramesUntilStopped()
    {
        using var camera = new VirtualCameraStreamService();
        var received = new TaskCompletionSource<ImageFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        camera.FrameReady += frame => received.TrySetResult(frame);

        camera.StartLiveView();
        var frame = await received.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(camera.ImageWidth, frame.Width);
        Assert.Equal(camera.ImageHeight, frame.Height);
        Assert.Equal(frame.Stride * frame.Height, frame.Pixels.Length);

        camera.StopLiveView();
    }

    [Fact]
    public async Task FiducialReturnsDetectedOffset()
    {
        var result = await new VirtualFiducialService().DetectFromCameraAsync();

        Assert.True(result.Found);
        Assert.InRange(result.OffsetX, -0.2, 0.2);
        Assert.InRange(result.OffsetY, -0.2, 0.2);
    }

    [Fact]
    public async Task BoltControllerCompletesCycle()
    {
        var bolt = new VirtualBoltService();

        await bolt.InitializeAsync();
        await bolt.ShootAsync();

        var result = await bolt.TightenAsync(1.2);

        Assert.True(result.Success);
        Assert.InRange(result.Torque, 1.152, 1.248);
    }

    [Fact]
    public async Task InspectionReturnsResultAndImage()
    {
        var outcome = await new VirtualInspectionService().InspectAsync();

        Assert.True(outcome.Result is InspectionResult.Good or InspectionResult.Ng);
        Assert.Equal(
            outcome.Image.Stride * outcome.Image.Height,
            outcome.Image.Pixels.Length);
    }
}
