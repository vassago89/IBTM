using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
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
    public void HardwareMapUsesNamedSignalsWithEditableNumbers()
    {
        var hardware = new HardwareMap();
        hardware.Inputs[InputIo.PcbPlacementCarrierJigPresent] = 57;
        hardware.Outputs[OutputIo.PcbPlacementStopperUp] = 58;
        hardware.Axes[MachineAxis.Conveyor] = 9;
        hardware.AxisDirections[MachineAxis.PcbPlacementX] =
            AxisDirection.Negative;
        hardware.OutputFeedbacks[OutputIo.PcbSupplyRotateToHandoff]
            .TimeoutMilliseconds = 1_500;

        var json = JsonSerializer.Serialize(hardware);
        var restored = JsonSerializer.Deserialize<HardwareMap>(json)!;

        Assert.Contains("\"PcbPlacementCarrierJigPresent\":57", json);
        Assert.Contains("\"PcbPlacementStopperUp\":58", json);
        Assert.Contains("\"Conveyor\":9", json);
        Assert.Contains("\"PcbPlacementX\":\"Negative\"", json);
        Assert.Contains("\"PcbSupplyRotateToHandoff\"", json);
        Assert.Contains("\"TimeoutMilliseconds\":1500", json);
        Assert.Equal(57, restored.Inputs[InputIo.PcbPlacementCarrierJigPresent]);
        Assert.Equal(58, restored.Outputs[OutputIo.PcbPlacementStopperUp]);
        Assert.Equal(9, restored.Axes[MachineAxis.Conveyor]);
        Assert.Equal(
            AxisDirection.Negative,
            restored.AxisDirections[MachineAxis.PcbPlacementX]);
        var rotation = restored.OutputFeedbacks[
            OutputIo.PcbSupplyRotateToHandoff];
        Assert.Equal(InputIo.PcbSupplyRotationHandoff, rotation.OnInput);
        Assert.Equal(InputIo.PcbSupplyRotationHome, rotation.OffInput);
        Assert.Equal(1_500, rotation.TimeoutMilliseconds);
    }

    [Fact]
    public void PcbSupplyRecipeStoresOnlyXAndZ()
    {
        var json = JsonSerializer.Serialize(new PcbSupplyRecipe());
        using var document = JsonDocument.Parse(json);
        var carrierPick = document.RootElement.GetProperty("CarrierPick1");

        Assert.True(carrierPick.TryGetProperty("X", out _));
        Assert.True(carrierPick.TryGetProperty("Z", out _));
        Assert.False(carrierPick.TryGetProperty("Y", out _));
    }

    [Fact]
    public void IoStoresInputsAndOutputs()
    {
        var io = new VirtualIoService();

        io.SetInput(InputIo.PcbPlacementHousing1Present, true);
        io.SetOutput(OutputIo.PcbPlacementLaser, true);

        Assert.True(io.GetInput(InputIo.PcbPlacementHousing1Present));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementLaser));

        io.TurnOffAll();

        Assert.False(io.GetOutput(OutputIo.PcbPlacementLaser));
    }

    [Fact]
    public async Task IoProvidesMachineSignalsAndActuatorFeedback()
    {
        var io = new VirtualIoService();
        io.Initialize();

        Assert.False(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.PcbSupplyCarrierAvailable));
        Assert.True(io.GetInput(InputIo.PcbSupplyRotationHome));
        Assert.False(io.GetInput(InputIo.PcbSupplyRotationHandoff));
        Assert.False(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.MainLaneUpstreamBoardAvailable));
        Assert.True(io.GetInput(InputIo.MainLaneDownstreamMachineReady));

        io.SetOutput(OutputIo.PcbPlacementGripper, true);
        await io.WaitForInputAsync(InputIo.PcbPlacementGripperClosed, true);
        Assert.True(io.GetInput(InputIo.PcbPlacementGripperClosed));
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbPresent));

        io.SetOutput(OutputIo.PcbPlacementGripper, false);
        await io.WaitForInputAsync(InputIo.PcbPlacementGripperClosed, false);
        Assert.False(io.GetInput(InputIo.PcbPlacementGripperClosed));

        io.SetOutput(OutputIo.PcbSupplyGripper, true);
        Assert.True(io.GetInput(InputIo.PcbSupplyGripperClosed));
        Assert.True(io.GetInput(InputIo.PcbSupplyPcbPresent));

        io.SetOutput(OutputIo.PcbPlacementGripper, true);
        Assert.True(io.GetInput(InputIo.PcbPlacementPcbPresent));

        io.SetOutput(OutputIo.PcbSupplyGripper, false);
        Assert.False(io.GetInput(InputIo.PcbSupplyPcbPresent));
        Assert.True(io.GetInput(InputIo.PcbPlacementPcbPresent));

        io.SetOutput(OutputIo.PcbPlacementGripper, false);
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbPresent));

        io.SetOutput(OutputIo.PcbSupplyRotateToHandoff, true);
        Assert.False(io.GetInput(InputIo.PcbSupplyRotationHome));
        Assert.True(io.GetInput(InputIo.PcbSupplyRotationHandoff));
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
            InputIo.PcbPlacementCarrierJigPresent,
            OutputIo.PcbPlacementStopperUp,
            OutputIo.PcbPlacementBackupPlateUp);
        var boltFastening = new CarrierJigPositioner(
            io,
            InputIo.BoltFasteningCarrierJigPresent,
            OutputIo.BoltFasteningStopperUp,
            OutputIo.BoltFasteningBackupPlateUp);
        var inspection = new CarrierJigPositioner(
            io,
            InputIo.InspectionCarrierJigPresent,
            OutputIo.InspectionStopperUp,
            OutputIo.InspectionBackupPlateUp);

        io.Initialize();
        conveyor.Initialize();
        pcbPlacement.Initialize();
        boltFastening.Initialize();
        inspection.Initialize();

        await conveyor.ReceiveAsync(pcbPlacement, CancellationToken.None);
        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.False(io.GetOutput(OutputIo.MainLaneUpstreamMachineReady));
        Assert.False(io.GetInput(InputIo.MainLaneUpstreamBoardAvailable));

        await conveyor.TransferAsync(
            pcbPlacement,
            boltFastening,
            CancellationToken.None);
        Assert.False(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.MainLaneUpstreamBoardAvailable));

        await conveyor.ReceiveAsync(pcbPlacement, CancellationToken.None);
        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));

        await conveyor.TransferAsync(
            boltFastening,
            inspection,
            CancellationToken.None);
        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));

        await conveyor.TransferAsync(
            pcbPlacement,
            boltFastening,
            CancellationToken.None);
        Assert.False(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));

        await conveyor.SendAsync(inspection, CancellationToken.None);
        Assert.False(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.MainLaneDownstreamMachineReady));

        Assert.False(servo.IsRunning);
        Assert.False(io.GetOutput(OutputIo.MainLaneDownstreamBoardAvailable));
        Assert.False(io.GetOutput(OutputIo.PcbPlacementStopperUp));
        Assert.False(io.GetOutput(OutputIo.BoltFasteningStopperUp));
        Assert.False(io.GetOutput(OutputIo.InspectionStopperUp));
    }

    [Fact]
    public async Task IoWaitsForInputChanges()
    {
        var io = new VirtualIoService();
        var waiting = io.WaitForInputAsync(
            InputIo.PcbPlacementHousing2Present,
            true);

        Assert.False(waiting.IsCompleted);

        io.SetInput(InputIo.PcbPlacementHousing2Present, true);
        await waiting;
    }

    [Fact]
    public async Task OutputFeedbackUsesConfiguredTimeout()
    {
        var hardware = new HardwareMap();
        hardware.OutputFeedbacks[OutputIo.PcbPlacementGripper]
            .TimeoutMilliseconds = 20;
        var virtualIo = new VirtualIoService(hardware);
        virtualIo.DisabledFeedbacks.Add(OutputIo.PcbPlacementGripper);
        IIoService io = virtualIo;

        var exception = await Assert.ThrowsAsync<IoFeedbackTimeoutException>(
            () => io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementGripper,
                true));

        Assert.Equal(OutputIo.PcbPlacementGripper, exception.Output);
        Assert.Equal(InputIo.PcbPlacementGripperClosed, exception.Input);
        Assert.True(exception.OutputValue);
        Assert.True(exception.InputValue);
    }

    [Fact]
    public async Task MotionMovesAndJogsAllAxes()
    {
        using var motion =
            new VirtualMotionService(new StationMotionSettings());
        motion.Initialize();
        var positionUpdates = 0;
        var stoppedUpdateReceived = false;
        motion.PositionChanged += (_, _, _) =>
        {
            positionUpdates++;
            stoppedUpdateReceived = motion.Axes.All(
                axis => motion.GetAxisState(axis).InPosition);
        };

        await motion.MoveToXYAsync(1, 2, 100);
        await motion.MoveToZAsync(3, 100);

        Assert.Equal((1, 2, 3), motion.GetPosition());
        Assert.True(positionUpdates > 2);
        Assert.True(stoppedUpdateReceived);
        await motion.MoveToSafeZAsync();
        motion.JogX(10);
        await Task.Delay(50);
        motion.Stop();

        Assert.True(motion.GetPosition().X > 1);
    }

    [Fact]
    public async Task PcbSupplyMotionUsesOnlyXAndZ()
    {
        using var motion = new VirtualMotionService(
            new StationMotionSettings(),
            hasY: false);
        motion.Initialize();

        await motion.MoveToXZAsync(10, 5);

        Assert.Equal([MotionAxis.X, MotionAxis.Z], motion.Axes);
        Assert.Equal((10, 0, 5), motion.GetPosition());
        Assert.Throws<InvalidOperationException>(() => motion.JogY(10));
    }

    [Fact]
    public async Task MotionForcesSafeZBeforeXyMovement()
    {
        var settings = new StationMotionSettings
        {
            SafeZ = -5,
            SpeedZ = 100,
        };
        using var motion = new VirtualMotionService(settings);
        motion.Initialize();
        await motion.MoveToZAsync(8, 100);

        var previous = motion.GetPosition();
        var unsafeMove = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != previous.X || y != previous.Y) && !motion.IsAtSafeZ)
            {
                unsafeMove = true;
            }

            previous = (x, y, z);
        };

        await motion.MoveToXYAsync(10, 20, 100);

        Assert.False(unsafeMove);
        Assert.Equal((10, 20, -5), motion.GetPosition());
    }

    [Fact]
    public async Task MotionRejectsXyJogOutsideSafeZ()
    {
        using var motion =
            new VirtualMotionService(new StationMotionSettings());
        motion.Initialize();
        await motion.MoveToZAsync(3, 100);

        Assert.Throws<InvalidOperationException>(() => motion.JogX(10));
        Assert.Throws<InvalidOperationException>(() => motion.JogY(10));
    }

    [Fact]
    public async Task MotionReportsStateAndHomesSelectedAxis()
    {
        var settings = new StationMotionSettings
        {
            SafeZ = -5,
            SpeedZ = 100,
        };
        using var motion = new VirtualMotionService(settings);
        motion.Initialize();
        await motion.MoveToXYAsync(5, 6, 100);
        await motion.MoveToZAsync(3, 100);
        var previous = motion.GetPosition();
        var unsafeHome = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if ((x != previous.X || y != previous.Y) && !motion.IsAtSafeZ)
            {
                unsafeHome = true;
            }

            previous = (x, y, z);
        };

        await motion.HomeAsync(MotionAxis.X, 100);

        Assert.False(unsafeHome);
        Assert.Equal((0, 6, -5), motion.GetPosition());
        Assert.True(motion.GetAxisState(MotionAxis.X).ServoOn);
        Assert.True(motion.GetAxisState(MotionAxis.X).Homed);
        Assert.True(motion.GetAxisState(MotionAxis.X).HomeSensor);

        motion.SetServo(MotionAxis.X, false);
        Assert.False(motion.GetAxisState(MotionAxis.X).ServoOn);

        motion.SetServo(MotionAxis.X, true);
        Assert.True(motion.GetAxisState(MotionAxis.X).ServoOn);
    }

    [Fact]
    public void ConveyorReportsServoState()
    {
        var io = new VirtualIoService();
        var conveyor = new VirtualConveyorServo(io);
        var running = false;
        conveyor.RunningChanged += value => running = value;

        conveyor.Initialize();

        Assert.True(conveyor.GetAxisState().ServoOn);
        Assert.True(conveyor.GetAxisState().Homed);

        conveyor.Run(100);
        Assert.True(running);

        conveyor.Stop();
        Assert.False(running);

        conveyor.SetServo(false);
        Assert.False(conveyor.GetAxisState().ServoOn);

        conveyor.SetServo(true);
        Assert.True(conveyor.GetAxisState().ServoOn);
    }

    [Fact]
    public async Task CameraStreamsFramesUntilStopped()
    {
        using var camera = new VirtualCameraStreamService(inspection: false);
        camera.Initialize();
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
        camera.Initialize();

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
        await bolt.SupplyAsync();

        var result = await bolt.TightenAsync(1.2);

        Assert.True(result.Success);
        Assert.InRange(result.Torque, 1.152, 1.248);
    }

    [Fact]
    public void CarrierInspectionIsNgWhenEitherPcbIsNg()
    {
        var image = new ImageFrame(1, 1, 1, [0]);
        var result = new CarrierInspectionResult(
            new PcbInspectionResult(InspectionResult.Good, image),
            new PcbInspectionResult(InspectionResult.Ng, image));

        Assert.Equal(InspectionResult.Ng, result.OverallResult);
    }
}
