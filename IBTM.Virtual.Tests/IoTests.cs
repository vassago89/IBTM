using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class IoTests
{
    [Fact]
    public async Task CylinderFeedbackRequiresOneEndpointAndRejectsContradictoryInputs()
    {
        var io = new VirtualIoService(
            new NgShuttleHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 100 });
        io.Initialize();
        io.AutoResponseEnabled = false;
        IIoService signals = io;
        io.SetInput(InputIo.NgShuttleDown, true);
        var waiting = signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        Assert.False(waiting.IsCompleted);
        io.SetInput(InputIo.NgShuttleUp, false);
        await waiting;

        io.SetInput(InputIo.NgShuttleUp, true);
        await Assert.ThrowsAsync<IoTimeoutException>(() =>
            signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true));
        Assert.True(io.GetOutput(OutputIo.NgShuttleDown));

        using var stop = new CancellationTokenSource();
        waiting = signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
    }

    [Fact]
    public async Task ReentrantOutputKeepsTheLatestFeedback()
    {
        var io = new VirtualIoService(
            new NgShuttleHardwareSettings().Outputs,
            new MachineOptions());
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgShuttleDown && value)
            {
                io.SetOutput(output, false);
            }
        };

        io.SetOutput(OutputIo.NgShuttleDown, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await ((IIoService)io).WaitForInputAsync(
            InputIo.NgShuttleUp,
            true,
            timeout.Token);

        Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
        Assert.False(io.GetInput(InputIo.NgShuttleDown));
        Assert.True(io.GetInput(InputIo.NgShuttleUp));
    }

    [Fact]
    public async Task ManualResponsePreservesOutputsAndResynchronizesOnlyCylinderFeedback()
    {
        var io = new VirtualIoService(
            new NgShuttleHardwareSettings().Outputs,
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        io.Initialize();
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(io.AutoResponseEnabled);

        io.SetOutput(OutputIo.NgShuttleDown, true);
        io.AutoResponseEnabled = false;
        await Task.Delay(300);

        Assert.True(io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(io.GetInput(InputIo.NgShuttleUp));
        Assert.False(io.GetInput(InputIo.NgShuttleDown));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));

        io.SetOutput(OutputIo.NgShuttleDown, false);
        io.SetOutput(OutputIo.NgShuttleDown, true);
        await Task.Delay(300);
        Assert.True(io.GetOutput(OutputIo.NgShuttleDown));
        Assert.False(io.GetInput(InputIo.NgShuttleDown));

        io.AutoResponseEnabled = true;
        Assert.False(io.GetInput(InputIo.NgShuttleDown));
        await Task.Delay(300);

        Assert.True(io.GetInput(InputIo.NgShuttleDown));
        Assert.False(io.GetInput(InputIo.NgShuttleUp));
        Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisablingDiscardsPendingCarrierTransferAndFeederRefills(bool reenableImmediately)
    {
        var io = new VirtualIoService(
            Outputs(
                new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(),
                new NgConveyorHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        io.Initialize();
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true);
        io.SetInput(InputIo.NgConveyorPosition3Occupied, true);
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ((IIoService)io).WaitForInputAsync(
            InputIo.PickupFeederBoltDetected, false, timeout.Token);
        Assert.True(io.GetInput(InputIo.PickupHeadVacuumDetected));

        io.SetOutput(OutputIo.NgConveyorRun, true);
        io.SetOutput(OutputIo.ShootingFeederRunSignal, true);
        io.AutoResponseEnabled = false;
        if (reenableImmediately)
        {
            io.AutoResponseEnabled = true;
        }
        await Task.Delay(400);

        Assert.True(io.GetOutput(OutputIo.NgConveyorRun));
        Assert.True(io.GetOutput(OutputIo.ShootingFeederRunSignal));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.False(io.GetInput(InputIo.ShootingFeederBoltDetected));

        io.AutoResponseEnabled = true;
        io.SetOutput(OutputIo.NgConveyorRun, false);
        io.SetOutput(OutputIo.ShootingFeederRunSignal, false);
        io.SetOutput(OutputIo.NgConveyorRun, true);
        io.SetOutput(OutputIo.ShootingFeederRunSignal, true);
        Assert.True(await WaitUntilAsync(
            () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                  && io.GetInput(InputIo.ShootingFeederBoltDetected),
            TimeSpan.FromSeconds(2)));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
    }

    [Fact]
    public async Task ManualResponseKeepsSensorEditsAndResumesFromTheCurrentGrip()
    {
        var io = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var motion = new VirtualMotionService(
            new MotionSettings(), new OperationCancellation(), hasY: false, hasZ: false);
        var machine = new VirtualMachine(io, [motion]);
        var buffer = new AxisPosition();
        motion.PositionChanged += (x, y, z) =>
        {
            machine.UpdateSupplyPosition(x, y, z, 0, (10, 0), (20, 0), buffer);
            machine.UpdatePlacementPosition(x, y, z, buffer);
        };
        io.Initialize();
        motion.Initialize();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbBufferPcbPresent, true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);

        await motion.MoveXAsync(10, 1_000);

        Assert.Equal(10, motion.GetPosition().X);
        Assert.True(io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
        Assert.True(io.GetInput(InputIo.PcbBufferPcbPresent));

        io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        await motion.MoveXAsync(0, 1_000);

        Assert.Equal(0, motion.GetPosition().X);
        Assert.False(io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbDetected));
        Assert.True(io.GetInput(InputIo.PcbBufferPcbPresent));

        foreach (var secured in new[] { true, false })
        {
            io.AutoResponseEnabled = false;
            io.SetOutput(OutputIo.PcbSupplyNestForward, secured);
            io.SetOutput(OutputIo.PcbSupplyIpmFixerForward, secured);
            io.SetOutput(OutputIo.PcbPlacementIpmGripperClose, secured);
            io.SetOutput(OutputIo.PcbPlacementVacuumEjector, secured);
            io.SetInput(InputIo.PcbSupplyPcbDetected, secured);
            io.SetInput(InputIo.PcbSupplyNestForward, secured);
            io.SetInput(InputIo.PcbSupplyNestBackward, !secured);
            io.SetInput(InputIo.PcbSupplyIpmFixerForward, secured);
            io.SetInput(InputIo.PcbSupplyIpmFixerBackward, !secured);
            io.SetInput(InputIo.PcbPlacementPcbDetected, secured);
            io.SetInput(InputIo.PcbPlacementIpmGripperClosed, secured);
            io.SetInput(InputIo.PcbPlacementIpmGripperOpen, !secured);
            io.SetInput(InputIo.PcbPlacementVacuumDetected, secured);
            io.SetInput(InputIo.PcbBufferPcbPresent, false);

            io.AutoResponseEnabled = true;
            await motion.MoveXAsync(secured ? 10 : 0, 1_000);

            Assert.Equal(secured, io.GetInput(InputIo.PcbSupplyPcbDetected));
            Assert.Equal(secured, io.GetInput(InputIo.PcbPlacementPcbDetected));
            Assert.False(io.GetInput(InputIo.PcbBufferPcbPresent));
        }

        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        io.SetInput(InputIo.PcbSupplyNestForward, true);
        io.SetInput(InputIo.PcbSupplyNestBackward, false);
        io.SetInput(InputIo.PcbSupplyIpmFixerForward, true);
        io.SetInput(InputIo.PcbSupplyIpmFixerBackward, false);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        io.SetInput(InputIo.PcbPlacementIpmGripperClosed, true);
        io.SetInput(InputIo.PcbPlacementIpmGripperOpen, false);
        io.SetInput(InputIo.PcbPlacementVacuumDetected, true);

        io.AutoResponseEnabled = true;
        await Task.Delay(300);
        await motion.MoveXAsync(10, 1_000);

        Assert.False(io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbDetected));
        Assert.False(io.GetInput(InputIo.PcbBufferPcbPresent));
    }

    [Fact]
    public void ManualResponseRetainsDoorEmergencyStopAndResetInterlocks()
    {
        var io = new VirtualIoService(
            new MachineHardwareSettings().Outputs, new MachineOptions());
        using var motion = new VirtualMotionService(
            new MotionSettings(), new OperationCancellation());
        _ = new VirtualMachine(io, [motion]);
        io.Initialize();
        motion.Initialize();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.Door1Open, true);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));

        io.SetInput(InputIo.AutoMode, true);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis =>
        {
            Assert.False(motion.GetAxisState(axis).ServoOn);
            Assert.True(motion.GetAxisState(axis).Alarm);
        });
        io.SetInput(InputIo.ResetButton, true);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));

        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).ServoOn));

        io.SetInput(InputIo.EmergencyStop1Pressed, true);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.False(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.False(motion.GetAxisState(axis).ServoOn));

        io.SetInput(InputIo.EmergencyStop1Pressed, false);
        io.SetInput(InputIo.ResetButton, false);
        io.SetInput(InputIo.ResetButton, true);
        Assert.True(io.GetInput(InputIo.ServoMainContactorOn));
        Assert.All(motion.Axes, axis => Assert.True(motion.GetAxisState(axis).ServoOn));
    }
}
