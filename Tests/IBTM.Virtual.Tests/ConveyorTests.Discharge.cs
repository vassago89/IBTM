using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed partial class ConveyorTests
{
    [Fact]
    public async Task NgCarrierWaitsAtInspectionForNgRemoval()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var placementWork = ConveyorStation.CreatePcbPlacement(io);
        var boltWork = ConveyorStation.CreateBoltFastening(io);
        var inspectionWork = CreateInspectionStation(io);
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork,
            new UnitSettings());
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp);
        virtualIo.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        var assembly = boltWork.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Shooting, 1, new BoltResult(false, 0));
        boltWork.Complete(boltWork.CurrentJob);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        var frontReadyBeforeTransfer = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorReadyToFront2
                && value
                && !io.GetInput(InputIo.InspectionHeatSink1Present))
            {
                frontReadyBeforeTransfer = true;
            }
        };

        var run = conveyor.RunAsync(cancellation.Token);
        Assert.True(await WaitUntilAsync(() => inspectionWork.IsAtInspectionPosition, TimeSpan.FromSeconds(3)));
        assembly.CompleteInspection();
        inspectionWork.Station.Complete(inspectionWork.Station.CurrentJob);
        await Task.Delay(100);

        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.Same(assembly, Assert.Single(inspectionWork.Station.Assemblies));
        Assert.Empty(boltWork.Assemblies);
        Assert.True(inspectionWork.HasNg);
        Assert.False(frontReadyBeforeTransfer);
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task DisabledNgTransferLetsNgCarrierLeaveAtRear()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var units = new UnitSettings { PcbPlacement = false, BoltFastening = false };
        var inspectionWork = CreateInspectionStation(io, units);
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            ConveyorStation.CreatePcbPlacement(io),
            ConveyorStation.CreateBoltFastening(io),
            inspectionWork,
            units);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.MainConveyorReadyFromRear && !value
                && io.GetOutput(OutputIo.MainConveyorRun))
            {
                discharged.TrySetResult();
            }
        };

        io.Initialize();
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.InspectionHeatSink1Present,
            OutputIo.InspectionBackupPlateUp);
        var assembly = inspectionWork.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, false);
        assembly.CompleteInspection();
        inspectionWork.Station.Complete(inspectionWork.Station.CurrentJob);
        Assert.True(inspectionWork.HasNg);
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        Assert.NotEqual(MainConveyorState.DischargingInspectionCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
        units.Inspection = false;
        Assert.Equal(MainConveyorState.WaitingForRearEquipment, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));

        using var cancellation = new CancellationTokenSource();
        var run = conveyor.RunAsync(cancellation.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorAvailableToRear),
                TimeSpan.FromSeconds(1)));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
            Assert.False(discharged.Task.IsCompleted);

            virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
            await discharged.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }

        Assert.False(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
    }

    [Fact]
    public async Task BlockedRearDischargeDoesNotBlockFrontReceiving()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(io, inspectionEnabled: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateDown, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateUp, true);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        VirtualTest.SetCarrier(virtualIo, InputIo.InspectionHeatSink1Present, true);
        var bothSmemaOutputsOn = false;
        io.OutputChanged += (_, _) => bothSmemaOutputsOn |= io.GetOutput(
            OutputIo.MainConveyorReadyToFront2)
            && io.GetOutput(OutputIo.MainConveyorAvailableToRear);

        var run = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, true);

        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));

        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        await io.WaitForInputAsync(InputIo.PcbPlacementBackupPlateUp, true);
        await io.WaitForInputAsync(InputIo.PcbPlacementStopperDown, true);
        await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, true);

        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.True(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        Assert.False(bothSmemaOutputsOn);

        cancellation.Cancel();
        await run;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 0.2)]
    [InlineData(true, 0.2)]
    public async Task DischargeStopsAfterRearReadyOffDelay(bool teaching, double delaySeconds)
    {
        var settings = new ConveyorSettings { RearSmemaOffDelaySeconds = delaySeconds };
        var (io, conveyor) = await PrepareRearDischargeAsync(settings);
        io.SetInput(InputIo.AutoMode, teaching);
        if (teaching)
        {
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            conveyor.TestDownstreamReady = true;
        }
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            await Task.Delay(50);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun)); // S3 clearing alone does not stop discharge.
            var elapsed = Stopwatch.StartNew();
            if (teaching)
                conveyor.TestDownstreamReady = false;
            else
                io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            if (delaySeconds > 0)
            {
                await Task.Delay(40);
                Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            }
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.True(elapsed.Elapsed.TotalSeconds >= delaySeconds - 0.02);
            await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, false);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DischargeObservesRearReadyOffPulseDuringMotorStart()
    {
        var settings = new ConveyorSettings { RearSmemaOffDelaySeconds = 0.2 };
        var (io, conveyor) = await PrepareRearDischargeAsync(settings);
        var elapsed = new Stopwatch();
        var stopped = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && !on && elapsed.IsRunning)
                stopped.TrySetResult(elapsed.Elapsed);
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            elapsed.Start();
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        };
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            var stopDelay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(stopDelay >= TimeSpan.FromSeconds(0.18), $"Stopped after {stopDelay.TotalSeconds:F3} s.");
            await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, false);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DischargeDoesNotStartIfRearReadyDropsWhileReleasingSupport()
    {
        var (io, conveyor) = await PrepareRearDischargeAsync();
        var started = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && !on)
                io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            if (output == OutputIo.MainConveyorRun && on)
                started = true;
        };
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, false);
            Assert.False(started);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DischargeTimesOutWhenRearReadyNeverTurnsOff()
    {
        var settings = new ConveyorSettings { TransferTimeoutSeconds = 0.2 };
        var (io, conveyor) = await PrepareRearDischargeAsync(settings);
        var error = await Assert.ThrowsAsync<IoTimeoutException>(() => conveyor.RunAsync());
        Assert.Equal(new IoTimeoutException(
            InputIo.MainConveyorReadyFromRear, false, 200).Message, error.Message);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DischargeRequiresPickupClearanceBeforeAndAfterSupportRelease(bool duringRelease)
    {
        var (io, conveyor) = await PrepareRearDischargeAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var started = false;
        var released = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && !on)
                released = true;
            if (duringRelease
                ? output == OutputIo.InspectionBackupPlateUp && !on
                : output == OutputIo.MainConveyorAvailableToRear && on)
            {
                io.SetInput(InputIo.NgCarrierPickupUp, false);
            }
            if (output == OutputIo.MainConveyorRun && on)
            {
                started = true;
                stop.Cancel();
            }
        };

        await Assert.ThrowsAsync<MotionInterlockException>(
            () => conveyor.RunAsync(stop.Token));
        Assert.False(started);
        Assert.Equal(duringRelease, released);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedDischargeDoesNotResumeRemainingDelay(bool cancel)
    {
        var (io, conveyor) = await PrepareRearDischargeAsync(
            new ConveyorSettings { RearSmemaOffDelaySeconds = 5 });
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            await Task.Delay(50);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            if (cancel)
                stop.Cancel();
            else
                conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        var started = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && on)
                started = true;
        };
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        try
        {
            await Task.Delay(100);
            Assert.False(started);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.Step);
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<(VirtualIoService Io, MainConveyor Conveyor)> PrepareRearDischargeAsync(
        ConveyorSettings? settings = null)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io, inspectionEnabled: false, settings: settings);
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        return (io, conveyor);
    }
}
