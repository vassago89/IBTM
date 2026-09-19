using System;
using System.Diagnostics;
using System.Linq;
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
    [Theory]
    [InlineData(InputIo.MainConveyorEntryCarrierDetected)]
    [InlineData(InputIo.MainConveyorAvailableFromFront2)]
    public async Task EitherEntrySensorOrFrontSmemaStartsReceiving(InputIo trigger)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.State);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback / work change:", StringComparison.Ordinal))
                waiting.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = conveyor.RunAsync(stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(conveyor.RunCommandOn);
            io.SetInput(trigger, true);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);

            Assert.Equal(MainConveyorState.ReceivingFrontCarrier, conveyor.State);
            Assert.Equal(trigger == InputIo.MainConveyorEntryCarrierDetected, conveyor.EntryCarrierDetected);
            Assert.Equal(trigger == InputIo.MainConveyorAvailableFromFront2, conveyor.UpstreamCarrierAvailable);
            Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateDown));
            Assert.True(io.GetInput(InputIo.PcbPlacementStopperUp));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task RepeatSeatsStation1AndWaitsForPlacementBeforeTransfer()
    {
        var io = CreateIo();
        var placement = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new());
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new()),
            CreateInspectionWork(io),
            new UnitSettings());
        io.Initialize();
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        var waitedForPlacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback / work change:", StringComparison.Ordinal)
                && placement.Station.CarrierSeated)
                waitedForPlacement.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(stop.Token, repeat: true);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.PcbPlacementHeatSink2Present, true));
            await waitedForPlacement.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(placement.Station.CarrierSeated);
            Assert.False(conveyor.RunCommandOn);
            Assert.False(placement.Completed);
            placement.Complete(placement.CurrentJob);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.Equal(StationCylinderState.Down, placement.Station.BackupPlate);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.PcbPlacementStopperUp)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp,
            OutputIo.BoltFasteningStopperUp)]
    [InlineData(InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp,
            OutputIo.InspectionStopperUp)]
    public async Task ForwardTransferWaitsForHeatSink2ThenPushesAgainstStopperForConfiguredDelay(
        InputIo destination,
        OutputIo backupPlate,
        OutputIo stopper)
    {
        var io = CreateIo(timeoutMilliseconds: 1_000);
        io.Initialize();
        Assert.Equal(3.0, new ConveyorSettings().CarrierStopDelaySeconds);
        var settings = new ConveyorSettings { CarrierStopDelaySeconds = 0.2 };
        var conveyor = CreateConveyor(
            io,
            placementEnabled: destination != InputIo.BoltFasteningHeatSink1Present,
            boltFasteningEnabled: destination != InputIo.InspectionHeatSink1Present,
            settings: settings);
        var transferState = destination switch
        {
            InputIo.PcbPlacementHeatSink1Present => MainConveyorState.ReceivingFrontCarrier,
            InputIo.BoltFasteningHeatSink1Present => MainConveyorState.MovingPcbPlacementToBoltFastening,
            _ => MainConveyorState.MovingBoltFasteningToInspection,
        };
        if (destination == InputIo.PcbPlacementHeatSink1Present)
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        }
        else
        {
            await SetSeatedCarrierAsync(
                io,
                io,
                destination == InputIo.BoltFasteningHeatSink1Present
                    ? InputIo.PcbPlacementHeatSink1Present
                    : InputIo.BoltFasteningHeatSink1Present,
                destination == InputIo.BoltFasteningHeatSink1Present
                    ? OutputIo.PcbPlacementBackupPlateUp
                    : OutputIo.BoltFasteningBackupPlateUp);
        }

        var elapsed = new Stopwatch();
        var stopped = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raisedWhileRunning = false;
        var raisedEmptyPlate = false;
        var stopperLoweredWhileRunning = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorRun && !value && elapsed.IsRunning)
                stopped.TrySetResult(elapsed.Elapsed);
            if (output == backupPlate && value && io.GetOutput(OutputIo.MainConveyorRun))
                raisedWhileRunning = true;
            if (value && output is OutputIo.PcbPlacementBackupPlateUp
                or OutputIo.BoltFasteningBackupPlateUp
                or OutputIo.InspectionBackupPlateUp)
            {
                var carrier = output switch
                {
                    OutputIo.PcbPlacementBackupPlateUp => InputIo.PcbPlacementHeatSink1Present,
                    OutputIo.BoltFasteningBackupPlateUp => InputIo.BoltFasteningHeatSink1Present,
                    _ => InputIo.InspectionHeatSink1Present,
                };
                raisedEmptyPlate |= !io.GetInput(carrier);
            }
            if (output == stopper && !value && io.GetOutput(OutputIo.MainConveyorRun))
                stopperLoweredWhileRunning = true;
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(
            cancellation.Token,
            repeat: destination == InputIo.InspectionHeatSink1Present);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            await Task.Delay(io.TimeoutMilliseconds + 100);
            Assert.False(run.IsCompleted);
            Assert.True(conveyor.RunCommandOn);
            Assert.Equal(transferState, conveyor.State);
            Assert.True(io.GetOutput(OutputIo.MainConveyorForward));
            Assert.False(io.GetOutput(backupPlate));
            Assert.True(io.GetOutput(stopper));
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            var second = destination switch
            {
                InputIo.PcbPlacementHeatSink1Present => InputIo.PcbPlacementHeatSink2Present,
                InputIo.BoltFasteningHeatSink1Present => InputIo.BoltFasteningHeatSink2Present,
                _ => InputIo.InspectionHeatSink2Present,
            };
            io.SetInput(destination, true);
            await Task.Delay(300);
            Assert.True(conveyor.RunCommandOn);
            Assert.False(io.GetOutput(backupPlate));
            elapsed.Start();
            io.SetInput(second, true);
            io.SetInput(second, false); // HS1 still proves presence after the HS2 arrival pulse.

            var delay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(delay >= TimeSpan.FromSeconds(0.18), $"Stopped after {delay.TotalSeconds:F3} s.");
            Assert.False(conveyor.RunCommandOn);
            if (destination == InputIo.InspectionHeatSink1Present)
            {
                Assert.True(await WaitUntilAsync(
                    () => conveyor.State == MainConveyorState.WaitingForInspection,
                    TimeSpan.FromSeconds(1)));
                Assert.False(io.GetOutput(backupPlate));
                Assert.True(io.GetOutput(stopper));
            }
            else
            {
                await WaitForOutputAsync(io, backupPlate, true);
                Assert.Equal(transferState, conveyor.State);
            }
            Assert.False(raisedWhileRunning);
            Assert.False(raisedEmptyPlate);
            Assert.False(stopperLoweredWhileRunning);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task InspectionReceivingIgnoresPickupHeightButWaitsForHeldCarrier()
    {
        var io = CreateIo();
        io.Initialize();
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var conveyor = CreateConveyor(io, boltFasteningEnabled: false);
        var steps = new System.Collections.Concurrent.ConcurrentQueue<string>();
        conveyor.Trace += steps.Enqueue;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(cancellation.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.PcbPlacementBackupPlateUp, false);
            Assert.True(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(conveyor.RunCommandOn);
            Assert.Equal(MainConveyorState.WaitingForInspectionClear, conveyor.State);
            Assert.True(await WaitUntilAsync(
                () => steps.Any(step => step.Contains("NG carrier detected=True")),
                TimeSpan.FromSeconds(1)));

            io.SetInput(InputIo.NgCarrierDetected, false);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(io.GetInput(InputIo.NgCarrierPickupUp));
            Assert.True(io.GetInput(InputIo.NgCarrierPickupDown));

            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.True(io.GetInput(InputIo.InspectionBackupPlateDown));
            Assert.True(io.GetInput(InputIo.InspectionStopperUp));
            Assert.False(conveyor.RunCommandOn);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ActiveTransferKeepsOriginalResultsWhenSourceGetsAnotherCarrier()
    {
        var io = CreateIo();
        IIoService signals = io;
        var source = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var destination = CreateInspectionWork(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new()), source, destination,
            new UnitSettings { NgCarrierTransfer = false });
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, signals, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var originalJob = source.CurrentJob;
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordPcbBolt(1, result);
        source.Complete(originalJob);
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            var nextJob = source.CurrentJob;
            Assert.NotSame(originalJob, nextJob);
            await source.Station.SeatAsync(default);
            // The equipment's HS1 pulses several times before HS2 confirms arrival.
            for (var pulse = 0; pulse < 3; pulse++)
            {
                io.SetInput(InputIo.InspectionHeatSink1Present, true);
                io.SetInput(InputIo.InspectionHeatSink1Present, false);
            }
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, false),
                (InputIo.InspectionHeatSink2Present, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.True(destination.AtInspectionPosition);

            Assert.Equal(originalJob.Id, destination.CurrentJob.Id);
            Assert.Same(assembly, Assert.Single(destination.Assemblies));
            Assert.Same(result, assembly.PcbBoltResults[1]);
            Assert.True(destination.HasNg);
            Assert.Same(nextJob, source.CurrentJob);
            Assert.Empty(source.Assemblies);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task TransferOwnsSourceLoweringThroughInspectionArrival()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var destination = CreateInspectionWork(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new()), source, destination,
            new UnitSettings { NgCarrierTransfer = false });
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        source.Complete(source.CurrentJob);
        io.AutoResponseEnabled = false;
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.InspectionStopperUp, true);
            Assert.True(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.True(source.Station.CarrierSeated);
            Assert.False(conveyor.RunCommandOn);
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.State);

            io.SetInputs(
                (InputIo.InspectionStopperDown, false),
                (InputIo.InspectionStopperUp, true));
            await WaitForOutputAsync(io, OutputIo.BoltFasteningBackupPlateUp, false);
            Assert.False(conveyor.RunCommandOn);
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.State);

            io.SetInputs(
                (InputIo.BoltFasteningBackupPlateUp, false),
                (InputIo.BoltFasteningBackupPlateDown, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.State);

            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.False(conveyor.RunCommandOn);
            Assert.True(await WaitUntilAsync(
                () => conveyor.State == MainConveyorState.WaitingForInspection,
                TimeSpan.FromSeconds(1)));
            Assert.True(destination.AtInspectionPosition);
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.True(io.GetOutput(OutputIo.InspectionStopperUp));
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(destination.InspectionRequested);
        Assert.Equal(MainConveyorState.PreparingInspectionCarrier, conveyor.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledStationLowersOnceAndKeepsTransferringWhileSourceSensorStaysOn(bool fastening)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(
            io, placementEnabled: fastening, boltFasteningEnabled: !fastening);
        var sourceInput = fastening
            ? InputIo.BoltFasteningHeatSink1Present
            : InputIo.PcbPlacementHeatSink1Present;
        var sourcePlate = fastening
            ? OutputIo.BoltFasteningBackupPlateUp
            : OutputIo.PcbPlacementBackupPlateUp;
        var destinationInput = fastening
            ? InputIo.InspectionHeatSink2Present
            : InputIo.BoltFasteningHeatSink2Present;
        var destinationPlate = fastening
            ? OutputIo.InspectionBackupPlateUp
            : OutputIo.BoltFasteningBackupPlateUp;
        var transferState = fastening
            ? MainConveyorState.MovingBoltFasteningToInspection
            : MainConveyorState.MovingPcbPlacementToBoltFastening;
        var raises = 0;
        var lowers = 0;
        io.Initialize();
        io.SetInput(sourceInput, true);
        io.OutputChanged += (output, on) =>
        {
            if (output != sourcePlate)
                return;
            if (on)
                raises++;
            else
                lowers++;
        };

        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.True(io.GetInput(sourceInput));
            Assert.Equal(transferState, conveyor.State);
            // Leave the source input ON long enough for a reseating loop to show itself.
            await Task.Delay(800);
            Assert.False(run.IsCompleted);
            Assert.True(conveyor.RunCommandOn);
            Assert.False(io.GetOutput(sourcePlate));
            Assert.Equal(1, raises);
            Assert.Equal(1, lowers);

            io.SetInput(sourceInput, false);
            io.SetInput(destinationInput, true);
            if (fastening)
            {
                await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
                Assert.False(io.GetOutput(destinationPlate));
                Assert.True(io.GetInput(InputIo.InspectionBackupPlateDown));
                Assert.True(io.GetInput(InputIo.InspectionStopperUp));
            }
            else
            {
                await WaitForOutputAsync(io, destinationPlate, true);
            }
            Assert.False(conveyor.RunCommandOn);
            Assert.Equal(1, raises);
            Assert.Equal(1, lowers);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DestinationPreparationFailureLeavesSourceCarrierRaised()
    {
        var io = CreateIo(timeoutMilliseconds: 1_000);
        var conveyor = CreateConveyor(io, boltFasteningEnabled: false);
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        io.AutoResponseEnabled = false;
        var releasedSource = false;
        var ran = false;
        io.OutputChanged += (output, on) =>
        {
            releasedSource |= output == OutputIo.BoltFasteningBackupPlateUp && !on;
            ran |= output == OutputIo.MainConveyorRun && on;
        };

        await Assert.ThrowsAsync<IoTimeoutException>(() => conveyor.RunAsync());

        Assert.False(releasedSource);
        Assert.False(ran);
        Assert.True(io.GetInput(InputIo.BoltFasteningBackupPlateUp));
        Assert.True(io.GetInput(InputIo.BoltFasteningHeatSink1Present));
        Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.State);
    }

    [Fact]
    public async Task CompletedRearCarrierMovesBeforeWaitingInfeed()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io, placementEnabled: false, boltFasteningEnabled: false, inspectionEnabled: false);
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);

        Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.State);

        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
        await SetSeatedCarrierAsync(
            io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        Assert.Equal(MainConveyorState.DischargingInspectionCarrier, conveyor.State);

        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        Assert.Equal(MainConveyorState.ReceivingFrontCarrier, conveyor.State);
    }

    [Fact]
    public async Task InterruptedTransferKeepsPendingResultsWithoutMovingThemOnLaterInput()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var destination = CreateInspectionWork(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new()), source, destination,
            new UnitSettings { NgCarrierTransfer = false });
        io.Initialize();
        await SetSeatedCarrierAsync(io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var job = source.CurrentJob;
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordPcbBolt(1, new BoltResult(false, 1.25));
        source.Complete(job);
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Same(job, source.CurrentJob);
        Assert.Same(assembly, Assert.Single(source.Assemblies));
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => destination.AtInspectionPosition, TimeSpan.FromSeconds(2)));
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(conveyor.RunCommandOn);
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Same(assembly, Assert.Single(source.Assemblies));
        Assert.Empty(destination.Assemblies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectedCarriersSeatIndependentlyAndReleaseOnlyCompletedWork(bool twoCarriers)
    {
        var io = CreateIo();
        var placement = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), new());
        var fastening = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            fastening,
            CreateInspectionWork(io),
            new UnitSettings { NgCarrierTransfer = false });
        io.Initialize();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        if (twoCarriers)
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var beltStarted = false;
        io.OutputChanged += (output, on) =>
        {
            beltStarted |= on && output == OutputIo.MainConveyorRun;
        };

        var run = conveyor.RunAsync();
        try
        {
            // Neither stopper has responded yet; both commands must already be issued.
            await WaitForOutputAsync(io, OutputIo.PcbPlacementStopperUp, true);
            if (twoCarriers)
                await WaitForOutputAsync(io, OutputIo.BoltFasteningStopperUp, true);
            io.SetInputs(
                (InputIo.PcbPlacementStopperDown, false),
                (InputIo.PcbPlacementStopperUp, true));
            await WaitForOutputAsync(io, OutputIo.PcbPlacementBackupPlateUp, true);
            io.SetInputs(
                (InputIo.PcbPlacementBackupPlateDown, false),
                (InputIo.PcbPlacementBackupPlateUp, true));
            await WaitForOutputAsync(io, OutputIo.PcbPlacementStopperUp, false);
            io.SetInputs(
                (InputIo.PcbPlacementStopperUp, false),
                (InputIo.PcbPlacementStopperDown, true));

            Assert.True(placement.Station.CarrierSeated);
            Assert.False(placement.Completed);
            Assert.False(fastening.Station.CarrierSeated);
            Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.False(beltStarted);

            if (twoCarriers)
            {
                io.AutoResponseEnabled = true;
                Assert.True(await WaitUntilAsync(
                    () => fastening.Station.CarrierSeated, TimeSpan.FromSeconds(2)));
                Assert.True(placement.Station.CarrierSeated);
                Assert.False(fastening.Completed);
                Assert.False(beltStarted);

                fastening.Complete(fastening.CurrentJob);
                await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.True(placement.Station.CarrierSeated);
                Assert.False(placement.Completed);
                Assert.Equal(StationCylinderState.Down, fastening.Station.BackupPlate);
            }
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
