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
        var placementWork = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new());
        var boltWork = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var inspectionWork = CreateInspectionWork(io);
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
        assembly.RecordPcbBolt(1, new BoltResult(false, 0));
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
        Assert.True(await WaitUntilAsync(() => inspectionWork.AtInspectionPosition, TimeSpan.FromSeconds(3)));
        assembly.CompleteInspection();
        inspectionWork.Complete(inspectionWork.CurrentJob);
        await Task.Delay(100);

        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.Same(assembly, Assert.Single(inspectionWork.Assemblies));
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
        var inspectionWork = CreateInspectionWork(io, units);
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), units),
            new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), units),
            inspectionWork,
            units);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExit = false;
        io.InputChanged += (input, value) =>
        {
            if (input != InputIo.MainConveyorExitCarrierDetected)
            {
                return;
            }

            reachedExit |= value;
            if (reachedExit && !value)
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
        var assembly = inspectionWork.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, false);
        assembly.CompleteInspection();
        inspectionWork.Complete(inspectionWork.CurrentJob);
        Assert.True(inspectionWork.HasNg);
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        Assert.NotEqual(MainConveyorState.DischargingInspectionCarrier, conveyor.State);
        units.NgCarrierTransfer = false;
        Assert.Equal(MainConveyorState.WaitingForRearEquipment, conveyor.State);

        using var cancellation = new CancellationTokenSource();
        var run = conveyor.RunAsync(cancellation.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorAvailableToRear),
                TimeSpan.FromSeconds(1)));
            Assert.False(conveyor.RunCommandOn);
            Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
            Assert.False(discharged.Task.IsCompleted);

            virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
            await discharged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            cancellation.Cancel();
            await run;
        }

        Assert.False(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(conveyor.RunCommandOn);
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
    [InlineData(false, false)] // Ready can fall before the exit sensor ever detects the carrier.
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DischargeStopsWhenRearReadyTurnsOff(bool teaching, bool carrierAtExit)
    {
        var (io, conveyor) = await PrepareRearDischargeAsync(carrierAtExit: carrierAtExit);
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
            if (teaching)
                conveyor.TestDownstreamReady = false;
            else
                io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.Equal(carrierAtExit, conveyor.ExitCarrierDetected);
            Assert.False(run.IsCompleted);
            if (carrierAtExit)
            {
                Assert.True(await WaitUntilAsync(
                    () => conveyor.State == MainConveyorState.WaitingForRearEquipment,
                    TimeSpan.FromSeconds(1)));
            }
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DischargeObservesExitPulseDuringMotorStart()
    {
        var settings = new ConveyorSettings { ExitSensorClearDelaySeconds = 0.2 };
        var (io, conveyor) = await PrepareRearDischargeAsync(settings);
        var elapsed = new Stopwatch();
        var stopped = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && !on && elapsed.IsRunning)
                stopped.TrySetResult(elapsed.Elapsed);
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            elapsed.Start();
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
        };
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            var stopDelay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(stopDelay >= TimeSpan.FromSeconds(0.18), $"Stopped after {stopDelay.TotalSeconds:F3} s.");
            await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, false);
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DischargeFiltersHolesFromFirstOffAndRequiresCurrentClear()
    {
        var settings = new ConveyorSettings();
        Assert.Equal(0.3, settings.ExitSensorClearDelaySeconds);
        var (io, conveyor) = await PrepareRearDischargeAsync(settings);
        using var stop = new CancellationTokenSource();
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            await Task.Delay(400);
            Assert.True(conveyor.RunCommandOn); // OFF before any detection is not an exit.
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
            await Task.Delay(400); // The ON duration does not count toward the hole margin.
            var elapsed = Stopwatch.StartNew();
            var stopped = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
            io.OutputChanged += (output, on) =>
            {
                if (output == OutputIo.MainConveyorRun && !on)
                    stopped.TrySetResult(elapsed.Elapsed);
            };
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
            await Task.Delay(40);
            Assert.True(conveyor.RunCommandOn);
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
            await Task.Delay(320);
            Assert.True(conveyor.RunCommandOn); // An earlier OFF pulse cannot override current ON.
            var lastClearAt = elapsed.Elapsed;
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
            var stopDelay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(stopDelay >= TimeSpan.FromSeconds(settings.ExitSensorClearDelaySeconds));
            Assert.True(stopDelay - lastClearAt < TimeSpan.FromSeconds(settings.ExitSensorClearDelaySeconds),
                "A later OFF must not restart the first-OFF margin.");
            Assert.False(run.IsCompleted);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DischargeStillFaultsWhenExitFeedbackNeverArrivesOrNeverClears(bool stuckOn)
    {
        var settings = new ConveyorSettings { ExitSensorClearDelaySeconds = 0.05 };
        var (io, conveyor) = await PrepareRearDischargeAsync(settings, timeoutMilliseconds: 1_000);
        io.SetInput(InputIo.MainConveyorExitCarrierDetected, stuckOn);

        var error = await Assert.ThrowsAsync<IoTimeoutException>(() => conveyor.RunAsync());
        Assert.Equal(new IoTimeoutException(
            InputIo.MainConveyorExitCarrierDetected, !stuckOn, io.TimeoutMilliseconds).Message, error.Message);

        Assert.False(conveyor.RunCommandOn);
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
    }

    [Fact]
    public async Task InterruptedDischargeRestartsFromCurrentExitInput()
    {
        var (io, conveyor) = await PrepareRearDischargeAsync();
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
        Assert.True(io.GetInput(InputIo.MainConveyorExitCarrierDetected));
        Assert.Equal(MainConveyorState.DischargingInspectionCarrier, conveyor.State);
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<(VirtualIoService Io, MainConveyor Conveyor)> PrepareRearDischargeAsync(
        ConveyorSettings? settings = null,
        bool carrierAtExit = false,
        int timeoutMilliseconds = 3_000)
    {
        var io = CreateIo(timeoutMilliseconds);
        var conveyor = CreateConveyor(io, inspectionEnabled: false, settings: settings);
        io.Initialize();
        if (carrierAtExit)
        {
            io.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
        }
        else
        {
            await SetSeatedCarrierAsync(
                io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
        }
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        return (io, conveyor);
    }
}
