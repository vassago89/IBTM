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
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed partial class ConveyorTests
{
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

    [Fact]
    public async Task StartupPreparationPreservesOccupiedSupportsWithoutTreatingNgDetectionAsGrip()
    {
        var io = CreateIo();
        var conveyor = CreateConveyor(io, ngCarrierTransferEnabled: true);
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
        Assert.False(conveyor.RunCommandOn);
        Assert.True(io.GetInput(other));
        using var idleStop = new CancellationTokenSource();
        var idle = conveyor.RunAsync(idleStop.Token);
        try
        {
            await WaitForOutputAsync(io, plate, true);
            Assert.False(conveyor.RunCommandOn);
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
            Assert.False(conveyor.RunCommandOn);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.State);
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
        Assert.Equal(MainConveyorState.WaitingForPcbPlacement, conveyor.State);
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
            Assert.False(conveyor.RunCommandOn);
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
            (InputIo.InspectionHeatSink1Present, severalOccupiedSensors),
            (InputIo.MainConveyorExitCarrierDetected, severalOccupiedSensors));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = conveyor.ReturnToStartAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
            Assert.False(io.GetOutput(OutputIo.MainConveyorForward));
            Assert.False(run.IsCompleted);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(conveyor.RunCommandOn);
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
            Assert.True(conveyor.RunCommandOn);
            if (stopBeforeEntry)
                stop.Cancel();
            else
                io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            // RUN must already be OFF in the same input/cancellation callback.
            Assert.False(conveyor.RunCommandOn);
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
        Assert.False(conveyor.RunCommandOn);
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

        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.State);
        Assert.False(conveyor.RunCommandOn);
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
            Assert.False(conveyor.RunCommandOn);
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
        ConveyorSettings? settings = null,
        bool ngCarrierTransferEnabled = false)
    {
        var units = new UnitSettings
        {
            PcbPlacement = placementEnabled,
            BoltFastening = boltFasteningEnabled,
            Inspection = inspectionEnabled,
            NgCarrierTransfer = ngCarrierTransferEnabled,
        };
        var placement = new PcbPlacementWork(ConveyorStation.CreatePcbPlacement(io), units);
        var fastening = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), units);
        var inspection = CreateInspectionWork(io, units);
        // Conveyor-only tests supply the completion normally reported by each station loop.
        foreach (var work in new StationWork[] { placement, fastening, inspection })
        {
            void CompleteDisabledWork()
            {
                if (!work.Enabled
                    && (work.Station.CarrierSeated
                        || ReferenceEquals(work, inspection) && inspection.AtInspectionPosition))
                    work.Complete(work.CurrentJob);
            }

            work.Changed += CompleteDisabledWork;
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

    private static InspectionWork CreateInspectionWork(IIoService io, UnitSettings? units = null)
    {
        var settings = new InspectionGantrySettings();
        var operations = new OperationCancellation();
        var motion = new VirtualMotionService(settings.Motion, operations, hasZ: false);
        motion.Initialize();
        var transfer = VirtualTest.CreateNgTransfer(io, motion, operations, settings);
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.CarrierImages =
            [new() { Number = 1, IsBarcode = true, HeatSink = HeatSinkSlot.HeatSink1, Center = new() }];
        return new InspectionWork(
            io, transfer, new NgCarrierTransferSettings { PickupSafeX = 0 }, recipes,
            units ?? new UnitSettings { MainConveyor = false, NgCarrierTransfer = false });
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
