using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Process;
using IBTM.Device;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
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

        Assert.False(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(PcbPlacementStation.PcbAvailableInputChannel));
        Assert.False(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(Conveyor.UpstreamBoardAvailableInputChannel));
        Assert.True(io.GetInput(Conveyor.DownstreamMachineReadyInputChannel));

        io.SetOutput(PcbPlacementStation.GripperChannel, true);
        await io.WaitForInputAsync(PcbPlacementStation.GripperChannel, true);
        Assert.True(io.GetInput(PcbPlacementStation.GripperChannel));

        io.SetOutput(PcbPlacementStation.GripperChannel, false);
        await io.WaitForInputAsync(PcbPlacementStation.GripperChannel, false);
        Assert.False(io.GetInput(PcbPlacementStation.GripperChannel));
    }

    [Fact]
    public async Task ConveyorMovesOneCarrierJigAtATimeAcrossOccupiedStations()
    {
        var io = new VirtualIoService();
        var servo = new VirtualConveyorServo(io);
        using var conveyor = new Conveyor(
            servo,
            io,
            new ConveyorSettings { Velocity = 100 });
        var pcbPlacement = new CarrierJigPositioner(
            io,
            PcbPlacementStation.CarrierJigPresentInputChannel,
            PcbPlacementStation.StopperUpOutputChannel,
            PcbPlacementStation.BackupPlateUpOutputChannel);
        var boltFastening = new CarrierJigPositioner(
            io,
            BoltFasteningStation.CarrierJigPresentInputChannel,
            BoltFasteningStation.StopperUpOutputChannel,
            BoltFasteningStation.BackupPlateUpOutputChannel);
        var inspection = new CarrierJigPositioner(
            io,
            InspectionStation.CarrierJigPresentInputChannel,
            InspectionStation.StopperUpOutputChannel,
            InspectionStation.BackupPlateUpOutputChannel);

        io.Initialize();
        conveyor.Initialize();
        pcbPlacement.Initialize();
        boltFastening.Initialize();
        inspection.Initialize();

        await conveyor.ReceiveAsync(pcbPlacement, CancellationToken.None);
        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetOutput(Conveyor.UpstreamMachineReadyOutputChannel));

        await conveyor.TransferAsync(
            pcbPlacement,
            boltFastening,
            CancellationToken.None);
        Assert.False(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        await conveyor.ReceiveAsync(pcbPlacement, CancellationToken.None);
        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));

        await conveyor.TransferAsync(
            boltFastening,
            inspection,
            CancellationToken.None);
        Assert.True(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        await conveyor.TransferAsync(
            pcbPlacement,
            boltFastening,
            CancellationToken.None);
        Assert.False(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        await conveyor.SendAsync(inspection, CancellationToken.None);
        Assert.False(io.GetInput(PcbPlacementStation.CarrierJigPresentInputChannel));
        Assert.True(io.GetInput(BoltFasteningStation.CarrierJigPresentInputChannel));
        Assert.False(io.GetInput(InspectionStation.CarrierJigPresentInputChannel));

        Assert.False(servo.IsRunning);
        Assert.False(io.GetOutput(Conveyor.DownstreamBoardAvailableOutputChannel));
        Assert.False(io.GetOutput(PcbPlacementStation.StopperUpOutputChannel));
        Assert.False(io.GetOutput(BoltFasteningStation.StopperUpOutputChannel));
        Assert.False(io.GetOutput(InspectionStation.StopperUpOutputChannel));
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
        using var camera = new VirtualCameraStreamService(inspection: false);
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
    public void CameraCapturesInspectionImage()
    {
        using var camera = new VirtualCameraStreamService(inspection: true);

        var image = camera.Capture();

        Assert.Equal(camera.ImageWidth, image.Width);
        Assert.Equal(camera.ImageHeight, image.Height);
        Assert.Equal(image.Stride * image.Height, image.Pixels.Length);
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
    public void CarrierInspectionIsNgWhenEitherPcbIsNg()
    {
        var image = new ImageFrame(1, 1, 1, [0]);
        var result = new CarrierInspectionResult(
            new InspectionOutcome(InspectionResult.Good, image),
            new InspectionOutcome(InspectionResult.Ng, image));

        Assert.Equal(InspectionResult.Ng, result.Result);
    }
}
