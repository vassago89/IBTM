using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class ConveyorTests
{
    [Fact]
    public async Task RepeatSeatsStation1AndWaitsForPlacementBeforeTransfer()
    {
        var io = CreateIo();
        var placement = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            new InspectionWork(ConveyorStation.Inspection(io), new NgCarrierTransfer(io)),
            routeInspectionToNg: () => true);
        io.Initialize();
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        await placement.Station.ReleaseAsync(default);
        var waitedForPlacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback / work change:", StringComparison.Ordinal)
                && placement.CarrierSeated)
                waitedForPlacement.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(stop.Token, repeat: true);
        try
        {
            await waitedForPlacement.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(placement.CarrierSeated);
            Assert.False(conveyor.RunCommandOn);
            Assert.False(placement.Completed);
            placement.Complete(placement.CurrentJob);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.Equal(StationCylinderState.Down, placement.BackupPlate);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SmemaTestSignalWakesConveyorWithoutChangingPhysicalInput(bool receive)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.MainConveyorExitCarrierDetected, !receive);
        Assert.False(conveyor.TestUpstreamCarrierAvailable);
        Assert.False(conveyor.TestDownstreamReady);
        var input = receive ? InputIo.MainConveyorAvailableFromFront2 : InputIo.MainConveyorReadyFromRear;
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
            if (receive)
                conveyor.TestUpstreamCarrierAvailable = true;
            else
                conveyor.TestDownstreamReady = true;
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
            Assert.False(io.GetInput(input));
            Assert.Equal(receive, conveyor.TestUpstreamCarrierAvailable);
            Assert.Equal(!receive, conveyor.TestDownstreamReady);
            Assert.False(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
            Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(conveyor.RunCommandOn);
        Assert.Equal(receive, conveyor.TestUpstreamCarrierAvailable);
        Assert.Equal(!receive, conveyor.TestDownstreamReady);

        io.SetInput(input, true);
        conveyor.TestUpstreamCarrierAvailable = false;
        conveyor.TestDownstreamReady = false;
        Assert.False(conveyor.UpstreamCarrierAvailable);
        Assert.False(conveyor.DownstreamReady);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(receive ? conveyor.UpstreamCarrierAvailable : conveyor.DownstreamReady);
        io.SetInput(input, false);
        Assert.False(conveyor.UpstreamCarrierAvailable);
        Assert.False(conveyor.DownstreamReady);
    }

    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present, InputIo.PcbPlacementHeatSink2Present)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present, InputIo.BoltFasteningHeatSink2Present)]
    [InlineData(InputIo.InspectionHeatSink1Present, InputIo.InspectionHeatSink2Present)]
    public async Task HeatSinkPresenceReportsOnlyCombinedCarrierEdges(InputIo heatSink1, InputIo heatSink2)
    {
        var io = CreateIo();
        var station = heatSink1 switch
        {
            InputIo.PcbPlacementHeatSink1Present => ConveyorStation.PcbPlacement(io),
            InputIo.BoltFasteningHeatSink1Present => ConveyorStation.BoltFastening(io),
            _ => ConveyorStation.Inspection(io),
        };
        var edges = new List<bool>();
        station.CarrierChanged += edges.Add;
        var work = new PcbPlacementWork(station);
        Assert.False(station.CarrierPresent);
        var arrival = station.WaitForCarrierAsync(default);
        io.SetInputs((heatSink1, true), (heatSink2, true));
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        var job = work.CurrentJob;
        work.Complete(job);

        io.SetInput(heatSink1, false);
        Assert.True(station.CarrierPresent);
        Assert.Same(job, work.CurrentJob);
        Assert.True(work.Completed);
        io.SetInputs((heatSink1, true), (heatSink2, false));
        Assert.True(station.CarrierPresent);
        Assert.Same(job, work.CurrentJob);
        Assert.True(work.Completed);
        Assert.Equal(new[] { true }, edges);

        io.SetInput(heatSink1, false);
        Assert.False(station.CarrierPresent);
        arrival = station.WaitForCarrierAsync(default);
        io.SetInput(heatSink2, true);
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotSame(job, work.CurrentJob);
        Assert.False(work.Completed);
        Assert.Equal(new[] { true, false, true }, edges);
    }

    [Fact]
    public async Task StartupPreparationPreservesOccupiedSupportsAndNgPickupSupport()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
        await SetSeatedCarrierAsync(io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        await SetSeatedCarrierAsync(io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
        var lowered = new List<OutputIo>();
        io.OutputChanged += (output, on) =>
        {
            if (!on && output is OutputIo.PcbPlacementBackupPlateUp
                or OutputIo.BoltFasteningBackupPlateUp or OutputIo.InspectionBackupPlateUp)
                lowered.Add(output);
        };

        await conveyor.PrepareEmptyStationsAsync(default);
        Assert.Empty(lowered);
        io.SetInput(InputIo.NgCarrierDetected, true);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        await conveyor.PrepareEmptyStationsAsync(default);
        Assert.Empty(lowered);

        io.SetInput(InputIo.NgCarrierDetected, false);
        await conveyor.PrepareEmptyStationsAsync(default);
        Assert.Equal(OutputIo.InspectionBackupPlateUp, Assert.Single(lowered));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
        Assert.True(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
    }

    [Fact]
    public void DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var destination = new InspectionWork(ConveyorStation.Inspection(io), new NgCarrierTransfer(io));
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.Assembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordPcbBolt(1, new BoltResult(false, 1.25));
        source.Complete(departing);

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var replacement = source.Assembly(HeatSinkSlot.HeatSink2);
        Assert.Throws<InvalidOperationException>(() => source.Complete(departing));
        Assert.Throws<InvalidOperationException>(() => source.Assembly(departing, HeatSinkSlot.HeatSink1));
        Assert.False(source.Completed);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        source.TransferAssembliesTo(destination, departing);
        Assert.Equal(departing.Id, destination.CurrentJob.Id);
        Assert.NotSame(departing, destination.CurrentJob);
        Assert.Same(original, Assert.Single(destination.Assemblies));
        Assert.True(destination.HasNg);
        Assert.False(destination.Completed);
        Assert.Same(replacement, Assert.Single(source.Assemblies));
    }

    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp,
        OutputIo.PcbPlacementStopperUp)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp,
        OutputIo.BoltFasteningStopperUp)]
    [InlineData(InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp,
        OutputIo.InspectionStopperUp)]
    public async Task ForwardTransferWaitsForArrivalThenPushesAgainstStopperForConfiguredDelay(
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
            io.SetInput(second, true);
            await Task.Delay(300);
            Assert.True(conveyor.RunCommandOn);
            Assert.False(io.GetOutput(backupPlate));
            elapsed.Start();
            io.SetInput(destination, true);

            var delay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(delay >= TimeSpan.FromSeconds(0.18), $"Stopped after {delay.TotalSeconds:F3} s.");
            Assert.False(conveyor.RunCommandOn);
            await WaitForOutputAsync(io, backupPlate, true);
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
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(cancellation.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.PcbPlacementBackupPlateUp, false);
            Assert.True(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(conveyor.RunCommandOn);

            io.SetInput(InputIo.NgCarrierDetected, false);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(io.GetInput(InputIo.NgCarrierPickupUp));
            Assert.True(io.GetInput(InputIo.NgCarrierPickupDown));

            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.InspectionBackupPlateUp, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.InspectionStopperDown, true);
            Assert.False(conveyor.RunCommandOn);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterArrivalFinishesSeatingPushBeforeRaisingPlate(bool fromFront)
    {
        var io = CreateIo();
        io.Initialize();
        var settings = new ConveyorSettings { CarrierStopDelaySeconds = 0.2 };
        var conveyor = CreateConveyor(io, placementEnabled: fromFront, settings: settings);
        var destination = fromFront ? InputIo.PcbPlacementHeatSink1Present : InputIo.BoltFasteningHeatSink1Present;
        var plate = fromFront ? OutputIo.PcbPlacementBackupPlateUp : OutputIo.BoltFasteningBackupPlateUp;
        if (fromFront)
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        else
            await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);

        var first = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            io.SetInput(destination, true);
            conveyor.Stop();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(plate));
            // Heat Sink 1 has passed its sensor, but the same carrier remains at Heat Sink 2.
            io.SetInputs(
                (fromFront ? InputIo.PcbPlacementHeatSink2Present : InputIo.BoltFasteningHeatSink2Present, true),
                (destination, false));
        }
        finally
        {
            conveyor.Stop();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var pushing = new Stopwatch();
        TimeSpan? pushed = null;
        var raisedWhileRunning = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && on)
                pushing.Restart();
            if (output == OutputIo.MainConveyorRun && !on && pushing.IsRunning)
            {
                pushed = pushing.Elapsed;
                pushing.Stop();
            }
            if (output == plate && on)
                raisedWhileRunning |= io.GetOutput(OutputIo.MainConveyorRun);
        };
        var resumed = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, plate, true);
            Assert.NotNull(pushed);
            Assert.True(pushed >= TimeSpan.FromSeconds(0.18), $"Resumed push lasted {pushed}.");
            Assert.False(raisedWhileRunning);
        }
        finally
        {
            conveyor.Stop();
            await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopDuringPlateRaiseResumesWithoutAnotherPushOrLoweringSupport()
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(io, placementEnabled: false,
            settings: new ConveyorSettings { CarrierStopDelaySeconds = 0.05 });
        await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
        io.OutputChanged += StopDuringRaise;
        var first = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetInput(InputIo.BoltFasteningBackupPlateUp));
            Assert.False(io.GetInput(InputIo.BoltFasteningBackupPlateDown));
        }
        finally
        {
            io.OutputChanged -= StopDuringRaise;
            conveyor.Stop();
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var repeatedPush = false;
        var loweredSupport = false;
        io.OutputChanged += (output, on) =>
        {
            repeatedPush |= output == OutputIo.MainConveyorRun && on;
            loweredSupport |= output == OutputIo.BoltFasteningBackupPlateUp && !on;
        };
        var resumed = conveyor.RunAsync();
        try
        {
            io.AutoResponseEnabled = true;
            await ((IIoService)io).WaitForInputAsync(InputIo.BoltFasteningStopperDown, true);
            Assert.True(io.GetInput(InputIo.BoltFasteningBackupPlateUp));
            Assert.False(repeatedPush);
            Assert.False(loweredSupport);
        }
        finally
        {
            conveyor.Stop();
            await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        }

        void StopDuringRaise(OutputIo output, bool on)
        {
            if (output != OutputIo.BoltFasteningBackupPlateUp || !on)
                return;
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
            conveyor.Stop();
        }
    }

    [Fact]
    public async Task LostCarrierStopsSeatingPushAndRestartPreservesRaisedSupport()
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(io,
            settings: new ConveyorSettings { CarrierStopDelaySeconds = 30 });
        var pushing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.Contains("target=seating push", StringComparison.Ordinal))
                pushing.TrySetResult();
        };
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            await pushing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("lost during the seating push", failure.Message);
            Assert.False(conveyor.RunCommandOn);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(MainConveyorState.CarrierPositionUnknown, conveyor.State);
        }
        finally
        {
            conveyor.Stop();
        }

        await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
        var unsafeOutput = false;
        io.OutputChanged += (output, on) =>
        {
            unsafeOutput |= (output == OutputIo.MainConveyorRun && on) || (output == OutputIo.PcbPlacementBackupPlateUp && !on);
        };
        var restartFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => conveyor.RunAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains("backup plate DOWN and stopper UP", restartFailure.Message);
        Assert.False(unsafeOutput);
        Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateUp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsCarrierArrivalOrSeatingDelayWithoutRaisingThePlate(bool arrived)
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(
            io,
            settings: new ConveyorSettings { CarrierStopDelaySeconds = 30 });
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.RunAsync(cancellation.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, arrived);
            await Task.Delay(50);
            Assert.True(conveyor.RunCommandOn);

            conveyor.Stop();

            Assert.False(conveyor.RunCommandOn);
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ReverseCarrierDetectionDoesNotUseForwardStopDelay()
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(
            io,
            settings: new ConveyorSettings { CarrierStopDelaySeconds = 30 });
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = conveyor.ReturnToStartAsync(cancellation.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorForward));
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            Assert.True(conveyor.RunCommandOn);
            Assert.False(run.IsCompleted);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);

            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(conveyor.RunCommandOn);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(OutputIo.PcbPlacementBackupPlateUp, OutputIo.PcbPlacementStopperUp,
        InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown, 50, 57)]
    [InlineData(OutputIo.BoltFasteningBackupPlateUp, OutputIo.BoltFasteningStopperUp,
        InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown, 54, 64)]
    [InlineData(OutputIo.InspectionBackupPlateUp, OutputIo.InspectionStopperUp,
        InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown, 58, 71)]
    public async Task StationCylindersOnRaisesAndOffLowers(
        OutputIo output,
        OutputIo stopper,
        InputIo up,
        InputIo down,
        int channel,
        int stopperDownInput)
    {
        var io = CreateIo();
        io.Initialize();
        var station = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => ConveyorStation.PcbPlacement(io),
            OutputIo.BoltFasteningBackupPlateUp => ConveyorStation.BoltFastening(io),
            _ => ConveyorStation.Inspection(io),
        };
        var settings = new ConveyorHardwareSettings();
        var hardware = settings.Outputs[output];
        Assert.Equal(channel, hardware.Number);
        Assert.Equal(channel + 1, hardware.OffNumber);
        Assert.Equal(up, hardware.Feedback!.OnInput);
        Assert.Equal(down, hardware.Feedback.OffInput);
        var stopperHardware = settings.Outputs[stopper];
        Assert.Equal(channel - 2, stopperHardware.Number);
        Assert.Equal(channel - 1, stopperHardware.OffNumber);
        Assert.Equal(stopperDownInput + 1, settings.Inputs[stopperHardware.Feedback!.OnInput]);
        Assert.Equal(stopperDownInput, settings.Inputs[stopperHardware.Feedback.OffInput!.Value]);

        await station.PrepareToReceiveAsync(CancellationToken.None);
        Assert.False(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        Assert.True(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Up, station.Stopper);

        await station.SeatAsync(CancellationToken.None);
        Assert.True(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Up, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);

        await station.ReleaseAsync(CancellationToken.None);
        Assert.False(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Down, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);

        await station.PrepareToReceiveAsync(CancellationToken.None);
        io.OutputChanged += (changedOutput, value) =>
        {
            Assert.True(changedOutput == output || changedOutput == stopper);
            if (changedOutput == stopper && !value)
            {
                Assert.Equal(StationCylinderState.Up, station.BackupPlate);
            }
        };

        await station.RaiseBackupPlateAsync(CancellationToken.None);
        Assert.True(io.GetOutput(output));
        Assert.Equal(StationCylinderState.Up, station.BackupPlate);
        Assert.False(io.GetOutput(stopper));
        Assert.Equal(StationCylinderState.Down, station.Stopper);
    }

    [Fact]
    public async Task ConveyorStopsMotorAndPreservesRunFailureWhenHandshakeCleanupFails()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        var running = false;
        var runError = new IOException("Conveyor transfer failed.");
        var cleanupError = new IOException("Handshake OFF failed.");
        void FailHandshakeOff(OutputIo output, bool value)
        {
            if (running && output == OutputIo.MainConveyorReadyToFront2 && !value)
                throw cleanupError;
        }

        io.OutputChanged += FailHandshakeOff;
        try
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => conveyor.RunControlledAsync(
                _ =>
                {
                    io.SetOutput(OutputIo.MainConveyorRun, true);
                    io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
                    io.SetOutput(OutputIo.MainConveyorAvailableToRear, true);
                    running = true;
                    return Task.FromException(runError);
                },
                CancellationToken.None));
            Assert.Equal(new[] { runError, cleanupError }, failure.InnerExceptions);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        }
        finally
        {
            io.OutputChanged -= FailHandshakeOff;
            conveyor.Stop();
        }

        await conveyor.RunControlledAsync(_ => Task.CompletedTask, CancellationToken.None);
    }

    [Fact]
    public async Task ReverseReturnStopsAtEntryInsteadOfStation1AndRejectsUnknownPosition()
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(io);
        io.Initialize();
        foreach (var source in new[] { InputIo.InspectionHeatSink1Present, InputIo.PcbPlacementHeatSink1Present })
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            io.SetInput(source, true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await conveyor.ReturnToStartAsync(stop.Token);
            Assert.False(stop.IsCancellationRequested);
            Assert.False(io.GetInput(source));
            Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
            Assert.True(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
            Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateDown));
            Assert.False(io.GetInput(InputIo.PcbPlacementBackupPlateUp));
            Assert.True(io.GetInput(InputIo.PcbPlacementStopperDown));
            Assert.False(conveyor.RunCommandOn);
        }

        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
        var ranWithoutCarrier = false;
        io.OutputChanged += (output, value) => ranWithoutCarrier |= output == OutputIo.MainConveyorRun && value;
        using var empty = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => conveyor.ReturnToStartAsync(empty.Token));
        Assert.False(ranWithoutCarrier);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivedCarrierIsNotLoweredAgainAfterRestart(bool toInspection)
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(
            io,
            placementEnabled: toInspection,
            boltFasteningEnabled: !toInspection);
        io.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        await SetSeatedCarrierAsync(
            io,
            io,
            toInspection ? InputIo.BoltFasteningHeatSink1Present : InputIo.PcbPlacementHeatSink1Present,
            toInspection ? OutputIo.BoltFasteningBackupPlateUp : OutputIo.PcbPlacementBackupPlateUp);
        var plateUp = toInspection ? InputIo.InspectionBackupPlateUp : InputIo.BoltFasteningBackupPlateUp;
        var plateOutput = toInspection ? OutputIo.InspectionBackupPlateUp : OutputIo.BoltFasteningBackupPlateUp;
        var stopperDown = toInspection ? InputIo.InspectionStopperDown : InputIo.BoltFasteningStopperDown;
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        void StopAtPlate(InputIo input, bool value)
        {
            if (input == plateUp && value)
            {
                firstStop.Cancel();
            }
        }

        io.InputChanged += StopAtPlate;
        await conveyor.RunAsync(firstStop.Token);
        io.InputChanged -= StopAtPlate;
        Assert.True(io.GetInput(plateUp));
        var loweredAgain = false;
        io.OutputChanged += (output, value) => loweredAgain |= output == plateOutput && !value;
        using var resumed = new CancellationTokenSource();
        var run = conveyor.RunAsync(resumed.Token);
        var seated = await WaitUntilAsync(
            () => io.GetInput(plateUp) && io.GetInput(stopperDown) && !conveyor.RunCommandOn,
            TimeSpan.FromSeconds(3));
        resumed.Cancel();
        await run;

        Assert.True(seated);
        Assert.False(loweredAgain);
    }

    [Fact]
    public async Task StoppedTransferKeepsResultsWhenAnotherCarrierReachesEntry()
    {
        var io = CreateIo();
        IIoService signals = io;
        var placement = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var source = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var destination = new InspectionWork(ConveyorStation.Inspection(io), new NgCarrierTransfer(io));
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            source,
            destination,
            routeInspectionToNg: () => false);
        io.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, false);
        await SetSeatedCarrierAsync(
            io, signals, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var assembly = source.Assembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordPcbBolt(1, result);
        source.Complete(source.CurrentJob);

        var firstRun = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        }
        finally
        {
            conveyor.Stop();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        }

        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        var resumed = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.True(io.GetInput(InputIo.PcbPlacementStopperUp));
            Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateDown));
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            await signals.WaitForInputAsync(InputIo.InspectionBackupPlateUp, true);

            Assert.Same(assembly, Assert.Single(destination.Assemblies));
            Assert.Same(result, assembly.PcbBoltResults[1]);
            Assert.True(destination.HasNg);
            Assert.Empty(source.Assemblies);
            Assert.Empty(placement.Assemblies);
        }
        finally
        {
            conveyor.Stop();
            await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void HeatSinkInputChangesDoNotEraseCompletionOrTransferredNg()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var destination = new InspectionWork(
            ConveyorStation.Inspection(io),
            new NgCarrierTransfer(io));
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = source.Assembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordPcbBolt(1, result);
        source.Complete(source.CurrentJob);
        var changes = 0;
        source.Changed += () => changes++;

        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, false), (InputIo.BoltFasteningHeatSink2Present, true));

        Assert.Equal(2, changes);
        Assert.True(source.Completed);
        Assert.True(source.HasNg);
        Assert.Same(result, assembly.PcbBoltResults[1]);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        source.TransferAssembliesTo(destination, source.CurrentJob);

        Assert.Empty(source.Assemblies);
        Assert.Same(assembly, Assert.Single(destination.Assemblies));
        Assert.False(destination.Completed);
        Assert.True(destination.HasNg);
        assembly.CompleteInspection();
        destination.Complete(destination.CurrentJob);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, false));
        Assert.True(destination.Completed);
        Assert.True(destination.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Empty(destination.Assemblies);
        Assert.False(destination.Completed);
        Assert.False(destination.HasNg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringMotorSetupCannotTurnRunBackOn(bool manual)
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stoppedDuringSetup = false;
        var started = false;
        io.OutputChanged += (output, value) =>
        {
            started |= output == OutputIo.MainConveyorRun && value;
            if (output == OutputIo.MainConveyorForward && value)
            {
                stoppedDuringSetup = true;
                stop.Cancel();
            }
        };

        if (manual)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => conveyor.RunMotorAsync(stop.Token));
        }
        else
        {
            await conveyor.RunAsync(stop.Token);
        }

        Assert.True(stoppedDuringSetup);
        Assert.False(started);
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task NgCarrierWaitsAtInspectionForNgRemoval()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var placementWork = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new NgCarrierTransfer(io));
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork,
            routeInspectionToNg: () => inspectionWork.RouteToNg);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp);
        var assembly = boltWork.Assembly(HeatSinkSlot.HeatSink1);
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
        await io.WaitForInputAsync(InputIo.InspectionBackupPlateUp, true);
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
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new NgCarrierTransfer(io));
        var ngTransferEnabled = true;
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.PcbPlacement(io), isEnabled: () => false),
            new BoltFasteningWork(ConveyorStation.BoltFastening(io), isEnabled: () => false),
            inspectionWork,
            routeInspectionToNg: () => ngTransferEnabled && inspectionWork.RouteToNg);
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
        var assembly = inspectionWork.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, false);
        assembly.CompleteInspection();
        inspectionWork.Complete(inspectionWork.CurrentJob);
        Assert.True(inspectionWork.HasNg);
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        Assert.NotEqual(MainConveyorState.DischargingInspectionCarrier, conveyor.State);
        ngTransferEnabled = false;
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

    [Fact]
    public async Task MainConveyorMovesTheRearMostRunnableCarrierFirst()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(io, placementEnabled: false, boltFasteningEnabled: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.PcbPlacementHeatSink1Present,
            OutputIo.PcbPlacementBackupPlateUp);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp);
        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(InputIo.InspectionHeatSink1Present, true);

        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.False(io.GetInput(InputIo.BoltFasteningHeatSink1Present));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task MainConveyorDoesNotInferCarrierFromCylinderPositions()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(io);

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await Task.WhenAll(
            io.SetOutputAndWaitAsync(OutputIo.PcbPlacementBackupPlateUp, false),
            io.SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, false),
            io.SetOutputAndWaitAsync(OutputIo.BoltFasteningBackupPlateUp, false),
            io.SetOutputAndWaitAsync(OutputIo.BoltFasteningStopperUp, true));

        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.State);
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task StoppedInfeedNeedsPresenceFeedbackBeforeResuming()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            new InspectionWork(ConveyorStation.Inspection(io), new NgCarrierTransfer(io)),
            routeInspectionToNg: () => false);

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        var firstRun = conveyor.RunAsync();
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        virtualIo.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        virtualIo.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);

        conveyor.Stop();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp);
        boltWork.Complete(boltWork.CurrentJob);

        using var cancellation = new CancellationTokenSource();
        var resumedRun = conveyor.RunAsync(cancellation.Token);
        Assert.Equal(MainConveyorState.CarrierPositionUnknown, conveyor.State);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        await io.WaitForInputAsync(InputIo.PcbPlacementBackupPlateUp, true);
        cancellation.Cancel();
        await resumedRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
    }

    [Fact]
    public async Task StoppedDischargeNeedsPresenceFeedbackBeforeResuming()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new NgCarrierTransfer(io));
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            inspectionWork,
            routeInspectionToNg: () => false);

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.InspectionHeatSink1Present,
            OutputIo.InspectionBackupPlateUp);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        inspectionWork.Assembly(HeatSinkSlot.HeatSink1).CompleteInspection();
        inspectionWork.Complete(inspectionWork.CurrentJob);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        var firstRun = conveyor.RunAsync();
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        VirtualTest.SetCarrier(virtualIo, InputIo.InspectionHeatSink1Present, false);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, false);

        conveyor.Stop();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.PcbPlacementHeatSink1Present,
            OutputIo.PcbPlacementBackupPlateUp);
        placementWork.Complete(placementWork.CurrentJob);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);

        using var cancellation = new CancellationTokenSource();
        var resumedRun = conveyor.RunAsync(cancellation.Token);
        Assert.Equal(MainConveyorState.CarrierPositionUnknown, conveyor.State);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        virtualIo.SetInput(InputIo.MainConveyorExitCarrierDetected, true);
        await WaitForOutputAsync(io, OutputIo.MainConveyorAvailableToRear, true);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        conveyor.Stop();
        await resumedRun.WaitAsync(TimeSpan.FromSeconds(2));

        var finalRun = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        virtualIo.SetInput(InputIo.MainConveyorExitCarrierDetected, false);
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);

        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await finalRun.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
    }

    [Fact]
    public void DestinationSensorDoesNotMoveWorkWithoutTransfer()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        _ = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            new InspectionWork(ConveyorStation.Inspection(io), new NgCarrierTransfer(io)),
            routeInspectionToNg: () => false);

        io.Initialize();
        var assembly = placementWork.Assembly(HeatSinkSlot.HeatSink1);
        VirtualTest.SetCarrier(virtualIo, InputIo.BoltFasteningHeatSink1Present, true);

        Assert.Same(assembly, Assert.Single(placementWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
    }

    [Fact]
    public void StationWorkReadsCurrentUnitSetting()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var enabled = false;
        var placementWork = new PcbPlacementWork(ConveyorStation.PcbPlacement(io), () => enabled);

        io.Initialize();
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);

        Assert.False(placementWork.Completed);
        placementWork.Complete(placementWork.CurrentJob); // Skipping a disabled station must not create a production completion.
        enabled = true;
        Assert.False(placementWork.Completed);
        enabled = false;
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateUp, true);
        Assert.True(placementWork.Completed);
        enabled = true;
        Assert.False(placementWork.Completed);
        enabled = false;
        Assert.True(placementWork.Completed);

        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, true);
        Assert.False(placementWork.Completed); // Contradictory feedback is not a completed rise.
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        Assert.False(placementWork.Completed);
    }

    [Fact]
    public void DisabledInspectionCanTransferCarrierAlreadyPresentAtStartup()
    {
        var io = CreateIo();
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);

        var work = new InspectionWork(
            ConveyorStation.Inspection(io),
            new NgCarrierTransfer(io),
            isEnabled: () => false);

        Assert.True(work.CarrierSeated);
        Assert.True(work.CanTransfer);
        Assert.True(work.RouteToNg);
        Assert.False(work.HasNg);
        work.Assembly(HeatSinkSlot.HeatSink1).RecordPcbBolt(1, new BoltResult(false, 1.25));
        Assert.True(work.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.False(work.CanTransfer);
        Assert.False(work.HasNg);
    }

    private static MainConveyor CreateConveyor(
        IIoService io,
        bool placementEnabled = true,
        bool boltFasteningEnabled = true,
        bool inspectionEnabled = true,
        ConveyorSettings? settings = null)
    {
        return new(
            io,
            settings ?? new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.PcbPlacement(io), () => placementEnabled),
            new BoltFasteningWork(ConveyorStation.BoltFastening(io), () => boltFasteningEnabled),
            new InspectionWork(
                ConveyorStation.Inspection(io),
                new NgCarrierTransfer(io),
                () => inspectionEnabled),
            routeInspectionToNg: () => false);
    }

    private static VirtualIoService CreateIo(int timeoutMilliseconds = 3_000)
    {
        var io = new VirtualIoService(
            Outputs(new ConveyorHardwareSettings(), new NgCarrierTransferHardwareSettings()),
            new MachineOptions { TimeoutMilliseconds = timeoutMilliseconds });
        io.SetInput(InputIo.AutoMode, false);
        return io;
    }

    private static async Task SetSeatedCarrierAsync(
        VirtualIoService virtualIo,
        IIoService io,
        InputIo carrier,
        OutputIo backupPlate)
    {
        virtualIo.SetInput(carrier, true);
        await io.SetOutputAndWaitAsync(backupPlate, true);
        var stopper = backupPlate switch
        {
            OutputIo.PcbPlacementBackupPlateUp => OutputIo.PcbPlacementStopperUp,
            OutputIo.BoltFasteningBackupPlateUp => OutputIo.BoltFasteningStopperUp,
            _ => OutputIo.InspectionStopperUp,
        };
        await io.SetOutputAndWaitAsync(stopper, false);
    }

}
