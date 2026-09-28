using System.Diagnostics;
using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SmemaTestSignalWakesConveyorWithoutChangingPhysicalInput(bool receive)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io, inspectionEnabled: false);
        io.Initialize();
        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        if (!receive)
            await SetSeatedCarrierAsync(io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
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
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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

    [Fact]
    public async Task StartupPreparationPreservesOccupiedSupportsWithoutTreatingNgDetectionAsGrip()
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
        Assert.Equal(OutputIo.InspectionBackupPlateUp, Assert.Single(lowered));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
        Assert.True(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSeatingRestartsFromCurrentPresenceWithoutReset(bool fromFront)
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(io, placementEnabled: fromFront,
            settings: new ConveyorSettings { CarrierStopDelaySeconds = 30 });
        var destination = fromFront ? InputIo.PcbPlacementHeatSink2Present : InputIo.BoltFasteningHeatSink2Present;
        var other = fromFront ? InputIo.PcbPlacementHeatSink1Present : InputIo.BoltFasteningHeatSink1Present;
        var plate = fromFront ? OutputIo.PcbPlacementBackupPlateUp : OutputIo.BoltFasteningBackupPlateUp;
        var pushing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.Contains("target=seating push", StringComparison.Ordinal))
                pushing.TrySetResult();
        };
        if (fromFront)
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        else
            await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);

        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            io.SetInput(destination, true);
            await pushing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.False(io.GetOutput(plate));
        io.SetInputs((other, true), (destination, false));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.True(io.GetInput(other));
        using var idleStop = new CancellationTokenSource();
        var idle = conveyor.RunAsync(idleStop.Token);
        try
        {
            await WaitForOutputAsync(io, plate, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            idleStop.Cancel();
            await idle.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task InterruptedPlateRaiseUsesFeedbackOnRestartWithoutLoweringSupport()
    {
        var io = CreateIo();
        io.Initialize();
        var conveyor = CreateConveyor(io, placementEnabled: false);
        await SetSeatedCarrierAsync(io, io, InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
        io.OutputChanged += StopDuringRaise;
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            io.SetInputs(
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink2Present, true));
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetInput(InputIo.BoltFasteningBackupPlateUp));
            Assert.False(io.GetInput(InputIo.BoltFasteningBackupPlateDown));
        }
        finally
        {
            io.OutputChanged -= StopDuringRaise;
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        var repeatedPush = false;
        var loweredSupport = false;
        io.OutputChanged += (output, on) =>
        {
            repeatedPush |= output == OutputIo.MainConveyorRun && on;
            loweredSupport |= output == OutputIo.BoltFasteningBackupPlateUp && !on;
        };
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        io.AutoResponseEnabled = true;
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        try
        {
            await WaitForOutputAsync(io, OutputIo.BoltFasteningStopperUp, false);
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(repeatedPush);
        Assert.False(loweredSupport);

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
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.PcbPlacementHeatSink2Present, true));
            await pushing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("lost during the seating push", failure.Message);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
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
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        Assert.Equal(MainConveyorState.WaitingForPcbPlacement, conveyor.Step);
        restartStop.Cancel();
        await restarted.WaitAsync(TimeSpan.FromSeconds(2));
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
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, arrived),
                (InputIo.PcbPlacementHeatSink2Present, arrived));
            await Task.Delay(50);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));

            conveyor.Stop();

            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(run.IsCompleted);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);

            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
            OutputIo.PcbPlacementBackupPlateUp => ConveyorStation.CreatePcbPlacement(io),
            OutputIo.BoltFasteningBackupPlateUp => ConveyorStation.CreateBoltFastening(io),
            _ => ConveyorStation.CreateInspection(io),
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

        var carrier = output switch
        {
            OutputIo.PcbPlacementBackupPlateUp => InputIo.PcbPlacementHeatSink1Present,
            OutputIo.BoltFasteningBackupPlateUp => InputIo.BoltFasteningHeatSink1Present,
            _ => InputIo.InspectionHeatSink1Present,
        };
        io.SetInput(carrier, true);
        Assert.False(station.CarrierSeated);
        await ((IIoService)io).SetOutputAndWaitAsync(output, true);
        Assert.Equal(StationCylinderState.Up, station.Stopper);
        Assert.True(station.CarrierSeated);

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

        await station.SeatAsync(CancellationToken.None);
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
            if (output == OutputIo.MainConveyorRun && value)
            {
                io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
                io.SetOutput(OutputIo.MainConveyorAvailableToRear, true);
                running = true;
                throw runError;
            }
            if (running && output == OutputIo.MainConveyorReadyToFront2 && !value)
                throw cleanupError;
        }

        io.OutputChanged += FailHandshakeOff;
        try
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => conveyor.RunMotorAsync());
            Assert.Equal(new[] { runError, cleanupError }, failure.InnerExceptions);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        }
        finally
        {
            io.OutputChanged -= FailHandshakeOff;
            conveyor.Stop();
        }

        using var cancellation = new CancellationTokenSource();
        var restarted = conveyor.RunMotorAsync(cancellation.Token);
        Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restarted);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
    }

    [Fact]
    public async Task ReverseReturnStopsAtEntryInsteadOfStation1()
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
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverseReturnUsesEntryFeedbackWithoutCountingCarriers(bool severalOccupiedSensors)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.SetInputs(
            (InputIo.PcbPlacementHeatSink1Present, severalOccupiedSensors),
            (InputIo.InspectionHeatSink1Present, severalOccupiedSensors));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = conveyor.ReturnToStartAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorForward));
            Assert.False(run.IsCompleted);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverseReturnIgnoresTransferTimeoutAndStopsImmediatelyAtEntryOrStop(bool stopBeforeEntry)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io,
            settings: new ConveyorSettings { TransferTimeoutSeconds = 0.05 });
        io.Initialize();
        using var stop = new CancellationTokenSource();
        var run = conveyor.ReturnToStartAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            await Task.Delay(150);
            Assert.False(run.IsCompleted);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            if (stopBeforeEntry)
                stop.Cancel();
            else
                io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            // RUN must already be OFF in the same input/cancellation callback.
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            if (stopBeforeEntry)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Fact]
    public async Task EntryDuringReverseMotorSetupCannotTurnRunOn()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.SetOutput(OutputIo.MainConveyorForward, true);
        var started = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorForward && !value)
                io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            started |= output == OutputIo.MainConveyorRun && value;
        };

        await conveyor.ReturnToStartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(started);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.True(io.GetOutput(OutputIo.MainConveyorNormalSpeed));
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

        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
    }

    [Fact]
    public async Task InterruptedInitialSeatingRestartsWithoutManualClear()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.PcbPlacementStopperUp, true);
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        io.SetInputs(
            (InputIo.PcbPlacementStopperDown, false),
            (InputIo.PcbPlacementStopperUp, true));
        io.AutoResponseEnabled = true;
        using var restartStop = new CancellationTokenSource();
        var restarted = conveyor.RunAsync(restartStop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.PcbPlacementBackupPlateUp, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static MainConveyor CreateConveyor(
        IIoService io,
        bool placementEnabled = true,
        bool boltFasteningEnabled = true,
        bool inspectionEnabled = true,
        ConveyorSettings? settings = null)
    {
        var units = new UnitSettings
        {
            PcbPlacement = placementEnabled,
            BoltFastening = boltFasteningEnabled,
            Inspection = inspectionEnabled,
        };
        var placement = ConveyorStation.CreatePcbPlacement(io);
        var fastening = ConveyorStation.CreateBoltFastening(io);
        var inspection = CreateInspectionStation(io, units);
        // Conveyor-only tests supply the completion normally reported by each station loop.
        foreach (var (station, enabled) in new[] { (placement, units.PcbPlacement), (fastening, units.BoltFastening), (inspection.Station, units.Inspection) })
        {
            void CompleteDisabledWork()
            {
                if (!enabled
                    && (station.CarrierSeated
                        || ReferenceEquals(station, inspection.Station) && inspection.IsAtInspectionPosition))
                    station.Complete(station.CurrentJob);
            }

            station.Changed += CompleteDisabledWork;
            CompleteDisabledWork();
        }
        return new(
            io,
            settings ?? new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            fastening,
            inspection,
            units);
    }

    private static InspectionStation CreateInspectionStation(IIoService io, UnitSettings? units = null)
    {
        var settings = new InspectionGantrySettings();
        var operations = new OperationCancellation();
        var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        return CreateNgTransfer(io, motion, operations, settings,
            new NgCarrierTransferSettings { CarrierPickupPosition = new(), WaitingPosition = new() },
            units ?? new UnitSettings { MainConveyor = false });
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

    [Fact]
    public async Task CarrierWaitRequiresPresenceAfterAShortArrivalPulse()
    {
        var io = CreateIo(timeoutMilliseconds: 100);
        var station = ConveyorStation.CreateInspection(io);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var arrival = station.WaitForCarrierAsync(default);
                io.SetInput(InputIo.InspectionHeatSink1Present, true);
                io.SetInput(InputIo.InspectionHeatSink1Present, false);
                await Assert.ThrowsAsync<TimeoutException>(() => arrival);

                io.SetInput(InputIo.InspectionHeatSink2Present, true);
                await station.WaitForCarrierAsync(default);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => station.WaitForCarrierAsync(new CancellationToken(canceled: true)));
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Fact]
    public void FirstPresenceNotificationAfterIoStartupKeepsTheExistingCarrierResults()
    {
        var io = CreateIo();
        io.IsReady = false;
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var station = ConveyorStation.CreatePcbPlacement(io);
        io.IsReady = true;
        var job = station.CurrentJob;
        var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
        station.Complete(job);

        // Physical I/O's initial scan has no arrival notification. A later
        // second sensor change must not replace the carrier already being worked.
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

        Assert.Same(job, station.CurrentJob);
        Assert.Same(assembly, Assert.Single(station.Assemblies));
        Assert.True(station.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingPresenceFromAnEarlierInputSubscriberDoesNotConsumeTheArrival(bool presentAtStartup)
    {
        var io = CreateIo();
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, presentAtStartup);
        ConveyorStation? station = null;
        io.InputChanged += (input, value) =>
        {
            if (station is not null)
                _ = station.CarrierPresent;
        };
        station = ConveyorStation.CreatePcbPlacement(io);
        var edges = new List<bool>();
        station.CarrierChanged += edges.Add;
        var initialJob = station.CurrentJob;

        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

        if (presentAtStartup)
        {
            Assert.Empty(edges);
            Assert.Same(initialJob, station.CurrentJob);
        }
        else
        {
            Assert.Equal(new[] { true }, edges);
            Assert.NotSame(initialJob, station.CurrentJob);
        }
    }

    [Fact]
    public void TransferRejectsReplacedDestinationWithoutChangingEitherJob()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = ConveyorStation.CreateInspection(io);
        io.Initialize();
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new(false, 1.25));
        SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        var arrived = destination.CurrentJob;

        SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        var replacementJob = destination.CurrentJob;
        var replacement = destination.GetAssembly(HeatSinkSlot.HeatSink2);
        destination.Complete(replacementJob);
        Assert.Throws<InvalidOperationException>(() =>
            source.TransferAssembliesTo(destination, departing, arrived));

        Assert.Same(departing, source.CurrentJob);
        Assert.Same(original, Assert.Single(source.Assemblies));
        Assert.Same(replacementJob, destination.CurrentJob);
        Assert.Same(replacement, Assert.Single(destination.Assemblies));
        Assert.True(destination.Completed);
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
            InputIo.PcbPlacementHeatSink1Present => ConveyorStation.CreatePcbPlacement(io),
            InputIo.BoltFasteningHeatSink1Present => ConveyorStation.CreateBoltFastening(io),
            _ => ConveyorStation.CreateInspection(io),
        };
        var edges = new List<bool>();
        station.CarrierChanged += edges.Add;
        Assert.False(station.CarrierPresent);
        var arrival = station.WaitForCarrierAsync(default);
        io.SetInputs((heatSink1, true), (heatSink2, true));
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        var job = station.CurrentJob;
        station.Complete(job);

        io.SetInput(heatSink1, false);
        Assert.True(station.CarrierPresent);
        Assert.Same(job, station.CurrentJob);
        Assert.True(station.Completed);
        io.SetInputs((heatSink1, true), (heatSink2, false));
        Assert.True(station.CarrierPresent);
        Assert.Same(job, station.CurrentJob);
        Assert.True(station.Completed);
        Assert.Equal(new[] { true }, edges);

        io.SetInput(heatSink1, false);
        Assert.False(station.CarrierPresent);
        arrival = station.WaitForCarrierAsync(default);
        io.SetInput(heatSink2, true);
        await arrival.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotSame(job, station.CurrentJob);
        Assert.False(station.Completed);
        Assert.Equal(new[] { true, false, true }, edges);
    }

    [Fact]
    public void DepartedWorkCannotCompleteNewCarrierAndTransferKeepsOriginalLoad()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var departing = source.CurrentJob;
        var original = source.GetAssembly(departing, HeatSinkSlot.HeatSink1);
        original.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new BoltResult(false, 1.25));
        source.Complete(departing);

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        var replacement = source.GetAssembly(HeatSinkSlot.HeatSink2);
        Assert.Throws<InvalidOperationException>(() => source.Complete(departing));
        Assert.Throws<InvalidOperationException>(() => source.GetAssembly(departing, HeatSinkSlot.HeatSink1));
        Assert.False(source.Completed);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        source.TransferAssembliesTo(destination.Station, departing, destination.Station.CurrentJob);
        Assert.Equal(departing.Id, destination.Station.CurrentJob.Id);
        Assert.NotSame(departing, destination.Station.CurrentJob);
        Assert.Same(original, Assert.Single(destination.Station.Assemblies));
        Assert.True(destination.HasNg);
        Assert.False(destination.Station.Completed);
        Assert.Same(replacement, Assert.Single(source.Assemblies));
    }

    [Fact]
    public void HeatSinkInputChangesDoNotEraseCompletionOrTransferredNg()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), result);
        source.Complete(source.CurrentJob);
        var changes = 0;
        source.Changed += () => changes++;

        io.SetInputs((InputIo.BoltFasteningHeatSink1Present, false), (InputIo.BoltFasteningHeatSink2Present, true));

        Assert.Equal(2, changes);
        Assert.True(source.Completed);
        Assert.True(source.HasNg);
        Assert.Same(result, assembly.PcbBoltResults[VirtualTest.BoltId(1)]);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        source.TransferAssembliesTo(destination.Station, source.CurrentJob, destination.Station.CurrentJob);

        Assert.Empty(source.Assemblies);
        Assert.Same(assembly, Assert.Single(destination.Station.Assemblies));
        Assert.False(destination.Station.Completed);
        Assert.True(destination.HasNg);
        assembly.CompleteInspection();
        destination.Station.Complete(destination.Station.CurrentJob);
        io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, false));
        Assert.True(destination.Station.Completed);
        Assert.True(destination.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        Assert.Empty(destination.Station.Assemblies);
        Assert.False(destination.Station.Completed);
        Assert.False(destination.HasNg);
    }

    [Fact]
    public void DestinationSensorDoesNotMoveWorkWithoutTransfer()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = ConveyorStation.CreatePcbPlacement(io);
        var boltWork = ConveyorStation.CreateBoltFastening(io);
        _ = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placementWork,
            boltWork,
            CreateInspectionStation(io),
            new UnitSettings { Inspection = false });

        io.Initialize();
        var assembly = placementWork.GetAssembly(HeatSinkSlot.HeatSink1);
        VirtualTest.SetCarrier(virtualIo, InputIo.BoltFasteningHeatSink1Present, true);

        Assert.Same(assembly, Assert.Single(placementWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
    }

    [Fact]
    public void StationCompletionBelongsToCurrentCarrierUntilReplacement()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = ConveyorStation.CreatePcbPlacement(io);

        io.Initialize();
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);

        Assert.False(placementWork.Completed);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateUp, true);
        Assert.False(placementWork.Completed); // Physical seating alone does not complete station work.
        placementWork.Complete(placementWork.CurrentJob);
        Assert.True(placementWork.Completed);

        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, true);
        Assert.False(placementWork.CarrierSeated);
        Assert.True(placementWork.Completed); // Position changes do not erase the owned result.
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, false);
        Assert.False(placementWork.Completed);
        VirtualTest.SetCarrier(virtualIo, InputIo.PcbPlacementHeatSink1Present, true);
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

        var work = CreateInspectionStation(io,
            new UnitSettings { Inspection = false });

        Assert.True(work.Station.CarrierSeated);
        Assert.False(work.IsTransferAllowed);
        work.Station.Complete(work.Station.CurrentJob);
        Assert.True(work.IsTransferAllowed);
        Assert.True(work.RouteToNg);
        Assert.False(work.HasNg);
        work.Station.GetAssembly(HeatSinkSlot.HeatSink1).RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new BoltResult(false, 1.25));
        Assert.True(work.HasNg);

        VirtualTest.SetCarrier(io, InputIo.InspectionHeatSink1Present, false);
        Assert.False(work.IsTransferAllowed);
        Assert.False(work.HasNg);
    }

    [Theory]
    [InlineData(InputIo.MainConveyorEntryCarrierDetected)]
    [InlineData(InputIo.MainConveyorAvailableFromFront2)]
    public async Task EitherEntrySensorOrFrontSmemaStartsReceiving(InputIo trigger)
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io);
        io.Initialize();
        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
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
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            io.SetInput(trigger, true);
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);

            Assert.Equal(MainConveyorState.ReceivingFrontCarrier, conveyor.Step);
            Assert.Equal(trigger == InputIo.MainConveyorEntryCarrierDetected, io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
            Assert.Equal(trigger == InputIo.MainConveyorAvailableFromFront2, conveyor.UpstreamCarrierAvailable);
            Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateDown));
            Assert.True(io.GetInput(InputIo.PcbPlacementStopperUp));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
    }

    [Theory]
    [InlineData(InputIo.MainConveyorEntryCarrierDetected)]
    [InlineData(InputIo.PcbPlacementHeatSink2Present)]
    [InlineData(InputIo.BoltFasteningHeatSink2Present)]
    [InlineData(InputIo.InspectionHeatSink2Present)]
    public async Task TransferTimeoutStopsBeltAndReportsMissingArrivalInput(InputIo missingInput)
    {
        var io = CreateIo(timeoutMilliseconds: 1_000);
        io.Initialize();
        var conveyor = CreateConveyor(
            io,
            placementEnabled: false,
            boltFasteningEnabled: false,
            settings: new ConveyorSettings { TransferTimeoutSeconds = 0.2 });
        switch (missingInput)
        {
            case InputIo.MainConveyorEntryCarrierDetected:
                io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
                break;
            case InputIo.PcbPlacementHeatSink2Present:
                io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
                break;
            case InputIo.BoltFasteningHeatSink2Present:
                await SetSeatedCarrierAsync(io, io,
                    InputIo.PcbPlacementHeatSink1Present, OutputIo.PcbPlacementBackupPlateUp);
                break;
            case InputIo.InspectionHeatSink2Present:
                await SetSeatedCarrierAsync(io, io,
                    InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
                break;
        }

        var run = conveyor.RunAsync();
        await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
        // HS1 alone must not complete or restart the HS2 arrival wait.
        switch (missingInput)
        {
            case InputIo.PcbPlacementHeatSink2Present:
                io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
                break;
            case InputIo.BoltFasteningHeatSink2Present:
                io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
                break;
            case InputIo.InspectionHeatSink2Present:
                io.SetInput(InputIo.InspectionHeatSink1Present, true);
                break;
        }
        var error = await Assert.ThrowsAsync<IoTimeoutException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new IoTimeoutException(missingInput, true, 200).Message, error.Message);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
        Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
        Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
    }

    [Fact]
    public async Task RepeatSeatsStation1AndWaitsForPlacementBeforeTransfer()
    {
        var io = CreateIo();
        var placement = ConveyorStation.CreatePcbPlacement(io);
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            ConveyorStation.CreateBoltFastening(io),
            CreateInspectionStation(io),
            new UnitSettings());
        io.Initialize();
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
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
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.PcbPlacementHeatSink2Present, true));
            await waitedForPlacement.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(placement.CarrierSeated);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
        Assert.Equal(5.0, new ConveyorSettings().TransferTimeoutSeconds);
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
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(transferState, conveyor.Step);
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
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(backupPlate));
            elapsed.Start();
            io.SetInput(second, true);
            io.SetInput(second, false); // HS1 still proves presence after the HS2 arrival pulse.

            var delay = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(delay >= TimeSpan.FromSeconds(0.18), $"Stopped after {delay.TotalSeconds:F3} s.");
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            if (destination == InputIo.InspectionHeatSink1Present)
            {
                Assert.True(await WaitUntilAsync(
                    () => conveyor.Step is MainConveyorState.WaitingForInspection,
                    TimeSpan.FromSeconds(1)));
                Assert.False(io.GetOutput(backupPlate));
                Assert.True(io.GetOutput(stopper));
            }
            else
            {
                await WaitForOutputAsync(io, backupPlate, true);
                Assert.Equal(transferState, conveyor.Step);
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
    public async Task InspectionReceivingDoesNotTreatNgDetectionAsHeldCarrier()
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
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
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
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
    public async Task StoppedInspectionArrivalRetainsFasteningNg(bool stopAtArrival)
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 30 }, new OperationCancellation(),
            ConveyorStation.CreatePcbPlacement(io), source, destination,
            new UnitSettings { Inspection = true });
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var originalJob = source.CurrentJob;
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new BoltResult(false, 1.25));
        source.Complete(originalJob);
        var pushing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.Contains("target=seating push", StringComparison.Ordinal))
                pushing.TrySetResult();
        };
        io.InputChanged += (input, value) =>
        {
            if (stopAtArrival && input == InputIo.InspectionHeatSink2Present && value)
                conveyor.Stop();
        };
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            if (!stopAtArrival)
                await pushing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.Equal(originalJob.Id, destination.Station.CurrentJob.Id);
        Assert.Same(assembly, Assert.Single(destination.Station.Assemblies));
        Assert.False(destination.Station.Completed);
        // Successful visual inspection after START must not erase the fastening NG.
        foreach (var slot in Enum.GetValues<HeatSinkSlot>().Where(destination.Station.IsHeatSinkPresent))
        {
            var inspected = destination.Station.GetAssembly(slot);
            inspected.RecordBoltPresence(VirtualTest.BoltId(1), true);
            inspected.CompleteInspection();
        }
        destination.Station.Complete(destination.Station.CurrentJob);
        Assert.True(destination.HasNg);
        Assert.True(destination.RouteToNg);
    }

    [Fact]
    public async Task ActiveTransferKeepsOriginalResultsWhenSourceGetsAnotherCarrier()
    {
        var io = CreateIo();
        IIoService signals = io;
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            ConveyorStation.CreatePcbPlacement(io), source, destination,
            new UnitSettings { Inspection = false });
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, signals, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var originalJob = source.CurrentJob;
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), result);
        source.Complete(originalJob);
        var runningWhenTransferred = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        destination.Station.Changed += () =>
        {
            if (destination.Station.CurrentJob.Id == originalJob.Id)
                runningWhenTransferred.TrySetResult(io.GetOutput(OutputIo.MainConveyorRun));
        };
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            var nextJob = source.CurrentJob;
            Assert.NotSame(originalJob, nextJob);
            await source.SeatAsync(default);
            // The equipment's HS1 pulses several times before HS2 confirms arrival.
            for (var pulse = 0; pulse < 3; pulse++)
            {
                io.SetInput(InputIo.InspectionHeatSink1Present, true);
                io.SetInput(InputIo.InspectionHeatSink1Present, false);
            }
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, false),
                (InputIo.InspectionHeatSink2Present, true));
            // Result subscribers must not run while the physical transfer is still powered.
            Assert.False(await runningWhenTransferred.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(destination.IsAtInspectionPosition);

            Assert.Equal(originalJob.Id, destination.Station.CurrentJob.Id);
            Assert.Same(assembly, Assert.Single(destination.Station.Assemblies));
            Assert.Same(result, assembly.PcbBoltResults[VirtualTest.BoltId(1)]);
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
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            ConveyorStation.CreatePcbPlacement(io), source, destination,
            new UnitSettings { Inspection = false });
        io.Initialize();
        await SetSeatedCarrierAsync(
            io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.BoltFasteningStopperUp, true);
        Assert.True(source.CarrierSeated);
        source.Complete(source.CurrentJob);
        Assert.True(source.Completed);
        io.AutoResponseEnabled = false;
        var inspectionWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback / work change:", StringComparison.Ordinal)
                && message.Contains(nameof(MainConveyorState.WaitingForInspection), StringComparison.Ordinal))
                inspectionWaiting.TrySetResult();
        };
        var run = conveyor.RunAsync();
        try
        {
            await WaitForOutputAsync(io, OutputIo.InspectionStopperUp, true);
            Assert.True(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.True(source.CarrierSeated);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.Step);

            io.SetInputs(
                (InputIo.InspectionStopperDown, false),
                (InputIo.InspectionStopperUp, true));
            await WaitForOutputAsync(io, OutputIo.BoltFasteningBackupPlateUp, false);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.Step);

            io.SetInputs(
                (InputIo.BoltFasteningBackupPlateUp, false),
                (InputIo.BoltFasteningBackupPlateDown, true));
            Assert.False(io.GetOutput(OutputIo.BoltFasteningStopperUp));
            Assert.True(io.GetInput(InputIo.BoltFasteningStopperUp));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            io.SetInputs(
                (InputIo.BoltFasteningStopperUp, false),
                (InputIo.BoltFasteningStopperDown, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
            Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.Step);
            Assert.Equal(MainConveyorState.Running, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));

            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, false);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(await WaitUntilAsync(
                () => conveyor.Step is MainConveyorState.WaitingForInspection,
                TimeSpan.FromSeconds(1)));
            await inspectionWaiting.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            Assert.Equal(MainConveyorState.WaitingForInspection, conveyor.Step);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            Assert.True(destination.IsAtInspectionPosition);
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.True(io.GetOutput(OutputIo.InspectionStopperUp));
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(destination.InspectionRequested);
        Assert.Null(conveyor.Step);
        Assert.Equal(MainConveyorState.PreparingInspectionCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
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
            Assert.Equal(transferState, conveyor.Step);
            // Leave the source input ON long enough for a reseating loop to show itself.
            await Task.Delay(800);
            Assert.False(run.IsCompleted);
            Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
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
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
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
        Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
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

        Assert.Equal(MainConveyorState.MovingBoltFasteningToInspection, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));

        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
        await SetSeatedCarrierAsync(
            io, io, InputIo.InspectionHeatSink1Present, OutputIo.InspectionBackupPlateUp);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        Assert.Equal(MainConveyorState.DischargingInspectionCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));

        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        Assert.Equal(MainConveyorState.ReceivingFrontCarrier, conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)));
    }

    [Fact]
    public async Task InterruptedTransferKeepsPendingResultsWithoutMovingThemOnLaterInput()
    {
        var io = CreateIo();
        var source = ConveyorStation.CreateBoltFastening(io);
        var destination = CreateInspectionStation(io);
        var conveyor = new MainConveyor(
            io, new ConveyorSettings { CarrierStopDelaySeconds = 0 }, new OperationCancellation(),
            ConveyorStation.CreatePcbPlacement(io), source, destination,
            new UnitSettings { Inspection = false });
        io.Initialize();
        await SetSeatedCarrierAsync(io, io, InputIo.BoltFasteningHeatSink1Present, OutputIo.BoltFasteningBackupPlateUp);
        var job = source.CurrentJob;
        var assembly = source.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new BoltResult(false, 1.25));
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
                () => destination.IsAtInspectionPosition, TimeSpan.FromSeconds(2)));
            Assert.False(io.GetOutput(OutputIo.InspectionBackupPlateUp));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Same(assembly, Assert.Single(source.Assemblies));
        Assert.Empty(destination.Station.Assemblies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectedCarriersSeatIndependentlyAndReleaseOnlyCompletedWork(bool twoCarriers)
    {
        var io = CreateIo();
        var placement = ConveyorStation.CreatePcbPlacement(io);
        var fastening = ConveyorStation.CreateBoltFastening(io);
        var conveyor = new MainConveyor(
            io,
            new ConveyorSettings { CarrierStopDelaySeconds = 0 },
            new OperationCancellation(),
            placement,
            fastening,
            CreateInspectionStation(io),
            new UnitSettings { Inspection = false });
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

            Assert.True(placement.CarrierSeated);
            Assert.False(placement.Completed);
            Assert.False(fastening.CarrierSeated);
            Assert.False(io.GetOutput(OutputIo.BoltFasteningBackupPlateUp));
            Assert.False(beltStarted);

            if (twoCarriers)
            {
                io.AutoResponseEnabled = true;
                Assert.True(await WaitUntilAsync(
                    () => fastening.CarrierSeated, TimeSpan.FromSeconds(2)));
                Assert.True(placement.CarrierSeated);
                Assert.False(fastening.Completed);
                Assert.False(beltStarted);

                fastening.Complete(fastening.CurrentJob);
                await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.True(placement.CarrierSeated);
                Assert.False(placement.Completed);
                Assert.Equal(StationCylinderState.Down, fastening.BackupPlate);
            }
        }
        finally
        {
            conveyor.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

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
        assembly.RecordBolt(FasteningHead.Shooting, VirtualTest.BoltId(1), new BoltResult(false, 0));
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
        assembly.RecordBoltPresence(VirtualTest.BoltId(1), false);
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
