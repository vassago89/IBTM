using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class NgConveyorTests
{
    [Fact]
    public async Task EjectPressDuringShuttleDescentDoesNotQueueAnEjection()
    {
        var system = CreateSystem();
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs(
            (InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgShuttleCarrierDetected, true));
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.LoweringShuttle, system.Conveyor.Step);
            Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            system.Io.SetInputs((InputIo.NgShuttleUp, false), (InputIo.NgShuttleDown, false));
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);

            var nextMove = new TaskCompletionSource<OutputIo>(TaskCreationOptions.RunContinuationsAsynchronously);
            system.Io.OutputChanged += (output, on) =>
            {
                if (output == OutputIo.NgShuttleDown && !on || output == OutputIo.NgConveyorRun && on)
                    nextMove.TrySetResult(output);
            };
            system.Io.SetInputs(
                (InputIo.NgConveyorStopperUp, true), (InputIo.NgConveyorStopperDown, false),
                (InputIo.NgShuttleDown, true));

            Assert.Equal(OutputIo.NgConveyorRun, await nextMove.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(NgConveyorState.MovingToPosition2, system.Conveyor.Step);
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HeldEjectButtonOnStartupRequiresANewPress()
    {
        var system = CreateSystem();
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgCarrierEjectButton, true));
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.Step);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            await Task.Delay(50);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task EjectionButtonLampsStayOffWhenDisabledOrRepeating(bool enabled, bool repeat)
    {
        var system = CreateSystem(units: new() { NgConveyor = enabled });
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true));
        system.Io.SetOutput(OutputIo.NgCarrierEjectLamp, true);
        system.Io.SetOutput(OutputIo.NgCarrierEjectCompleteLamp, true);
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token, repeat);
        try
        {
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            await Task.Delay(50);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
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
    public async Task OneEnableControlsShuttleAndBelt(bool enabled)
    {
        var system = CreateSystem(units: new() { NgConveyor = enabled });
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var shuttleCommands = 0;
        var beltStarts = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgShuttleDown && on)
                shuttleCommands++;
            if (output == OutputIo.NgConveyorRun && on)
                beltStarts++;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            if (enabled)
                await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true, stop.Token);
            else
                Assert.Equal(NgConveyorState.WaitingForCarrier, system.Conveyor.Step);
            Assert.Equal(enabled ? 1 : 0, shuttleCommands);
            Assert.Equal(enabled ? 1 : 0, beltStarts);
        }
        finally
        {
            stop.Cancel();
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Null(system.Conveyor.Step);
    }

    [Fact]
    public void ReceiveRequiresAvailableConveyorAndRaisedEmptyShuttle()
    {
        var system = CreateSystem();
        Assert.True(system.Conveyor.IsReceiveAllowed);
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
        Assert.False(system.Conveyor.IsReceiveAllowed);
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
        Assert.True(system.Conveyor.IsReceiveAllowed);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.False(system.Conveyor.IsReceiveAllowed);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleUp, false);
        system.Io.SetInput(InputIo.NgShuttleDown, true);
        Assert.False(system.Conveyor.IsReceiveAllowed);
    }

    [Fact]
    public async Task RepeatEndWaitsForShuttleStageCompletionAndWakesOnStepChange()
    {
        var system = CreateSystem();
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs(
            (InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgShuttleUp, false),
            (InputIo.NgShuttleDown, true));
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token, repeat: true);
        var end = system.Conveyor.WaitForRepeatEndAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.RaisingShuttle, system.Conveyor.Step);
            Assert.False(end.IsCompleted);
            system.Io.SetInputs((InputIo.NgShuttleDown, false), (InputIo.NgShuttleUp, true));
            await end.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.Step);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Null(system.Conveyor.Step);
    }

    [Fact]
    public async Task EmptyNgRepeatCanBeStoppedWhileWaiting()
    {
        var system = CreateSystem();
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunRepeatAsync(stop.Token);

        Assert.False(run.IsCompleted);
        Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task EmptyNgRepeatWaitsThenStartsWhenACarrierArrives()
    {
        var system = CreateSystem();
        using var stop = new CancellationTokenSource();
        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgShuttleDown && on)
                lowering.TrySetResult();
        };
        var run = system.Conveyor.RunRepeatAsync(stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));

            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task NgReverseReturnLowersShuttleBeforeStartingBelt()
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        var beltStarted = false;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on)
            {
                Assert.Equal(StationCylinderState.Down, system.Conveyor.ShuttleLift);
                beltStarted = true;
            }
        };
        // Allow the existing five-second equipment-debug delay after arrival.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        Assert.True(beltStarted);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.Equal(StationCylinderState.Up, system.Conveyor.ShuttleLift);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgReverseReturnUsesShuttleFeedbackWithoutCountingCarriers(bool severalOccupiedSensors)
    {
        var system = CreateSystem();
        await system.Conveyor.SetShuttleDownAsync(true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs(
            (InputIo.NgConveyorPosition1Occupied, severalOccupiedSensors),
            (InputIo.NgConveyorPosition2Occupied, severalOccupiedSensors));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            Assert.True(system.Io.GetOutput(OutputIo.NgConveyorReverse));
            Assert.False(run.IsCompleted);
            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            Assert.True(await WaitUntilAsync(
                () => !system.Io.GetOutput(OutputIo.NgShuttleDown), TimeSpan.FromSeconds(7)));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            system.Io.SetInputs((InputIo.NgShuttleDown, false), (InputIo.NgShuttleUp, true));
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(severalOccupiedSensors, system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.Equal(severalOccupiedSensors, system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
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
    public async Task NgReturnStopsBeltImmediatelyDuringArrivalSettling(bool losePickupClearance)
    {
        var system = CreateSystem();
        await system.Conveyor.SetShuttleDownAsync(true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            // Cancel after arrival, while the existing settling delay is still active.
            await Task.Delay(100);
            Assert.False(run.IsCompleted);
            Assert.True(system.Io.GetOutput(OutputIo.NgConveyorRun));
            if (losePickupClearance)
                system.Io.SetInput(InputIo.NgCarrierPickupUp, false);
            else
                stop.Cancel();
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task PickupFeedbackAlreadyBeingPublishedCanFinishAfterReturnIsCancelled()
    {
        var system = CreateSystem();
        await system.Conveyor.SetShuttleDownAsync(true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, false);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        using var stop = new CancellationTokenSource();
        using var releaseFeedback = new ManualResetEventSlim();
        var feedbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void PauseFeedback()
        {
            if (!system.Pickup.IsRaised && feedbackEntered.TrySetResult())
            {
                if (!releaseFeedback.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The test did not release the pickup feedback.");
            }
        }

        system.Pickup.Changed += PauseFeedback;
        var run = system.Conveyor.ReturnFromConveyorAsync(stop.Token);
        Task? feedback = null;
        try
        {
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            feedback = Task.Run(() => system.Io.SetInput(InputIo.NgCarrierPickupUp, false));
            await feedbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            releaseFeedback.Set();
            await feedback.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseFeedback.Set();
            system.Pickup.Changed -= PauseFeedback;
            stop.Cancel();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (feedback is not null)
                await feedback.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgReturnStopsBeforeBeltWhenCancelledOrPickupDropsDuringDescent(bool losePickupClearance)
    {
        var system = CreateSystem();
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        using var stop = new CancellationTokenSource();
        var downCommands = 0;
        var upCommands = 0;
        var motorStarts = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on)
                motorStarts++;
            if (output != OutputIo.NgShuttleDown)
                return;
            if (!on)
            {
                upCommands++;
                return;
            }
            downCommands++;
            if (losePickupClearance)
                system.Io.SetInput(InputIo.NgCarrierPickupUp, false);
            else
                stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Conveyor.ReturnFromConveyorAsync(stop.Token));
        Assert.Equal(1, downCommands);
        Assert.Equal(0, upCommands);
        Assert.Equal(0, motorStarts);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
    }

    [Fact]
    public async Task NgActuatorOutputsOnLowerPickupCloseGripperAndLowerShuttle()
    {
        var system = CreateSystem();
        var pickup = system.Pickup;

        await pickup.SetLiftUpAsync(false);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(StationCylinderState.Down, pickup.Lift);
        await pickup.SetLiftUpAsync(true);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.Equal(StationCylinderState.Up, pickup.Lift);

        await pickup.SetGripperOpenAsync(false);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.Equal(NgTransferGripperState.Closed, pickup.Gripper);
        await pickup.SetGripperOpenAsync(true);
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.Equal(NgTransferGripperState.Open, pickup.Gripper);

        await system.Conveyor.SetShuttleDownAsync(true);
        Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleDown));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleUp));
        await system.Conveyor.SetShuttleDownAsync(false);
        Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));
        Assert.False(system.Io.GetInput(InputIo.NgShuttleDown));
    }

    [Fact]
    public async Task EjectRunsFromS1ThenFromS2OncePerPressAndWaitsForComplete()
    {
        var hardware = new NgConveyorHardwareSettings();
        var stopper = hardware.Outputs[OutputIo.NgConveyorStopperUp];
        Assert.Equal(70, stopper.Number);
        Assert.Equal(71, stopper.OffNumber);
        Assert.Equal(InputIo.NgConveyorStopperUp, stopper.Feedback!.OnInput);
        Assert.Equal(InputIo.NgConveyorStopperDown, stopper.Feedback.OffInput);
        Assert.Equal(88, hardware.Inputs[stopper.Feedback.OnInput]);
        Assert.Equal(87, hardware.Inputs[stopper.Feedback.OffInput!.Value]);
        var system = CreateSystem(ejectRunSeconds: 0.35);
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true));
        var starts = 0;
        var shuttleDowns = 0;
        long startedAt = 0;
        long reachedS1At = 0;
        var runMilliseconds = 0d;
        var afterS1Milliseconds = 0d;
        system.Io.InputChanged += (input, value) =>
        {
            if (input == InputIo.NgConveyorPosition1Occupied && value)
                reachedS1At = Stopwatch.GetTimestamp();
        };
        system.Io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgShuttleDown && value)
                shuttleDowns++;
            if (output != OutputIo.NgConveyorRun)
                return;
            if (value)
            {
                starts++;
                startedAt = Stopwatch.GetTimestamp();
                Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperDown));
                Assert.False(system.Io.GetInput(InputIo.NgConveyorStopperUp));
            }
            else if (startedAt != 0)
            {
                runMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                if (reachedS1At != 0)
                    afterS1Milliseconds = Stopwatch.GetElapsedTime(reachedS1At).TotalMilliseconds;
                startedAt = 0;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            // Button guidance is independent of the full-carrier alarm threshold.
            Assert.False(system.Conveyor.AlarmRequired);
            Assert.True(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            Assert.False(system.Conveyor.IsReceiveAllowed);
            // Repeated presses during the timed run must not queue more ejections.
            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
            Assert.True(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.InRange(runMilliseconds, 330, 1500);
            Assert.False(system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperUp));
            await Task.Delay(100);
            Assert.Equal(1, starts);
            Assert.Equal(0, shuttleDowns);
            Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.Step);
            Assert.False(system.Conveyor.IsReceiveAllowed);
            await Assert.ThrowsAsync<MotionInterlockException>(() => system.Conveyor.SetShuttleDownAsync(true));

            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
            await Task.Delay(50);
            Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.Step);
            // S2 stays in place until a new EJECT press, without COMPLETE between them.
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.Equal(2, starts);
            Assert.InRange(afterS1Milliseconds, 330, 1500);
            Assert.True(runMilliseconds >= afterS1Milliseconds + 150);
            Assert.False(system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.False(system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
            Assert.False(system.Conveyor.IsReceiveAllowed);
            Assert.Equal(0, shuttleDowns);

            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
            Assert.False(system.Conveyor.IsReceiveAllowed);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
            Assert.True(await WaitUntilAsync(() => system.Conveyor.IsReceiveAllowed, TimeSpan.FromSeconds(1)));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task TimedEjectionStopsWithS1StillOnAndKeepsLoadedShuttleUp()
    {
        var system = CreateSystem(ejectRunSeconds: 0.1);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true), (InputIo.NgShuttleCarrierDetected, true));
        var shuttleDowns = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorStopperUp)
                system.Io.SetInputs((InputIo.NgConveyorStopperUp, on), (InputIo.NgConveyorStopperDown, !on));
            if (output == OutputIo.NgShuttleDown && on)
                shuttleDowns++;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.Full, system.Conveyor.Step);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            Assert.True(await WaitUntilAsync(
                () => system.Conveyor.Step is NgConveyorState.WaitingForEjectConfirmation, TimeSpan.FromSeconds(1)));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperUp));
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            await Task.Delay(50);
            Assert.Equal(0, shuttleDowns);
            Assert.Equal(StationCylinderState.Up, system.Conveyor.ShuttleLift);
            Assert.False(system.Conveyor.IsReceiveAllowed);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.Conveyor.ReturnFromConveyorAsync(stop.Token));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task EjectionKeepsS1PulseDuringMotorStart()
    {
        var system = CreateSystem(ejectRunSeconds: 0.1);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true));
        var starts = 0;
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorStopperUp)
                system.Io.SetInputs((InputIo.NgConveyorStopperUp, on), (InputIo.NgConveyorStopperDown, !on));
            if (output == OutputIo.NgConveyorRun && on && ++starts == 2)
            {
                system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
                system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
                system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
            await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
            system.Io.SetInput(InputIo.NgCarrierEjectButton, true);

            Assert.True(await WaitUntilAsync(
                () => starts == 2 && !system.Io.GetOutput(OutputIo.NgConveyorRun)
                    && system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp), TimeSpan.FromSeconds(1)));
            Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.Step);
            Assert.False(system.Conveyor.IsReceiveAllowed);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task StopDuringTimedEjectionKeepsShuttleBlockedUntilFreshComplete()
    {
        var system = CreateSystem(ejectRunSeconds: 5);
        system.Io.SetInputs((InputIo.NgConveyorPosition1Occupied, true),
            (InputIo.NgConveyorPosition2Occupied, true));
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
        stop.Cancel();
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        await run.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Conveyor.ReturnFromConveyorAsync(stop.Token));
        await system.Conveyor.RunAsync(stop.Token, repeat: true);
        system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        using var restartStop = new CancellationTokenSource();
        var restarted = system.Conveyor.RunAsync(restartStop.Token);
        try
        {
            Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.Step);
            Assert.True(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
            Assert.True(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
            Assert.False(system.Conveyor.IsReceiveAllowed);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(system.Io.GetOutput(OutputIo.NgShuttleDown));
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
            await Task.Delay(50);
            Assert.Equal(NgConveyorState.WaitingForEjectConfirmation, system.Conveyor.Step);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
            system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
            Assert.True(await WaitUntilAsync(
                () => system.Conveyor.Step is NgConveyorState.ReadyToEject, TimeSpan.FromSeconds(1)));
            Assert.True(system.Io.GetInput(InputIo.NgConveyorStopperUp));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        }
        finally
        {
            restartStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));
        Assert.False(system.Io.GetOutput(OutputIo.NgCarrierEjectCompleteLamp));
    }

    [Fact]
    public async Task StoppedCompactionKeepsUnknownLocationUntilPresenceReturns()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true);
        system.Io.AutoResponseEnabled = false;
        system.Io.SetInput(InputIo.NgShuttleUp, true);
        system.Io.SetInput(InputIo.NgShuttleDown, false);
        system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
        using var stop = new CancellationTokenSource();
        void StopBetweenSensors(OutputIo output, bool value)
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, false);
                stop.Cancel();
            }
        }

        system.Io.OutputChanged += StopBetweenSensors;
        await system.Conveyor.RunAsync(stop.Token);
        system.Io.OutputChanged -= StopBetweenSensors;
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.Null(system.Conveyor.Step);
        Assert.Equal(NgConveyorState.CarrierPositionUnknown, system.Conveyor.GetNextStep(system.Io.GetOutput(OutputIo.NgConveyorRun)));
        Assert.False(system.Conveyor.IsReceiveAllowed);
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        Assert.True(system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        using var nextStop = new CancellationTokenSource();
        var nextRun = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            Assert.Equal(NgConveyorState.ReadyToEject, system.Conveyor.Step);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        }
        finally
        {
            nextStop.Cancel();
            await nextRun.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopDuringMotorSetupCannotTurnRunBackOn()
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        system.Io.SetOutput(OutputIo.NgConveyorReverse, true);
        using var stop = new CancellationTokenSource();
        var started = false;
        system.Io.OutputChanged += (output, value) =>
        {
            started |= output == OutputIo.NgConveyorRun && value;
            if (output == OutputIo.NgConveyorReverse && !value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);
        Assert.True(stop.IsCancellationRequested);
        Assert.False(started);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.True(system.Io.GetOutput(OutputIo.NgConveyorNormalSpeed));
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoresRearFirstEjectsAndCompacts(bool stopAfterEject)
    {
        var system = CreateSystem(alarmCarrierCount: 2);
        using var cancellation = new CancellationTokenSource();
        var runs = system.RunAsync(cancellation.Token);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true);

        await LoadShuttleAsync(system);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition2Occupied, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectLamp, true);

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.True(system.Conveyor.AlarmRequired);
        Assert.False(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));

        await LoadShuttleAsync(system, lower: false);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(system.Conveyor.Full);

        void StopAfterEject(InputIo input, bool value)
        {
            if (stopAfterEject && input == InputIo.NgConveyorPosition1Occupied && !value)
            {
                cancellation.Cancel();
            }
        }

        system.Io.InputChanged += StopAfterEject;
        system.Io.SetInput(InputIo.NgCarrierEjectButton, true);
        if (stopAfterEject)
        {
            await runs;
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
            Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));
            Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
            system.Io.InputChanged -= StopAfterEject;
            return;
        }

        system.Io.InputChanged -= StopAfterEject;

        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, true);
        Assert.True(system.Io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(system.Io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(system.Io.GetInput(InputIo.NgShuttleUp));

        Assert.Equal(2, system.Conveyor.CarrierCount);
        Assert.True(system.Io.GetOutput(OutputIo.NgCarrierEjectLamp));

        system.Io.SetInput(InputIo.NgCarrierEjectButton, false);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        system.Io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectCompleteLamp, false);
        // Compaction now starts only after COMPLETE, including its existing settling time.
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, false, 10_000, cancellation.Token);
        await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition2Occupied, true);
        await WaitForOutputAsync(system.Io, OutputIo.NgCarrierEjectLamp, true);

        cancellation.Cancel();
        await runs;
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition1Occupied)]
    [InlineData(InputIo.NgConveyorPosition2Occupied)]
    public async Task ArrivedCarrierWaitsForShuttleClearInsteadOfRepeatingMove(InputIo destination)
    {
        var system = CreateSystem();
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgConveyorStopperUp, true);
        system.Io.AutoResponseEnabled = false;
        if (destination == InputIo.NgConveyorPosition2Occupied)
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        using var stop = new CancellationTokenSource();
        var run = system.Conveyor.RunAsync(stop.Token);
        try
        {
            await WaitForOutputAsync(system.Io, OutputIo.NgConveyorRun, true);
            system.Io.SetInput(destination, true);
            Assert.True(await WaitUntilAsync(
                () => system.Conveyor.Step is NgConveyorState.WaitingForShuttleUp,
                TimeSpan.FromSeconds(7)));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Conveyor.IsReceiveAllowed);

            system.Io.SetInput(InputIo.NgShuttleCarrierDetected, false);
            await WaitForOutputAsync(system.Io, OutputIo.NgShuttleDown, false);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition1Occupied, false)]
    [InlineData(InputIo.NgConveyorPosition1Occupied, true)]
    [InlineData(InputIo.NgConveyorPosition2Occupied, false)]
    public async Task InterruptedDestinationRestartsFromCurrentSensors(InputIo destination, bool stopAtDestination)
    {
        var system = CreateSystem();
        if (destination == InputIo.NgConveyorPosition2Occupied)
        {
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        }

        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);

        using var stop = new CancellationTokenSource();
        var motorStarts = 0;
        system.Io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                motorStarts++;
                if (!stopAtDestination)
                {
                    stop.Cancel();
                }
            }
        };
        system.Io.InputChanged += (input, value) =>
        {
            if (stopAtDestination && input == destination && value)
            {
                stop.Cancel();
            }
        };

        await system.Conveyor.RunAsync(stop.Token);

        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.Equal(1, motorStarts);
        using var nextStop = new CancellationTokenSource();
        var restarted = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            await system.Signals.WaitForInputAsync(destination, true, nextStop.Token);
        }
        finally
        {
            nextStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.Equal(stopAtDestination ? 1 : 2, motorStarts);
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition1Occupied, InputIo.NgShuttleCarrierDetected)]
    [InlineData(InputIo.NgConveyorPosition2Occupied, InputIo.NgShuttleCarrierDetected)]
    [InlineData(InputIo.NgConveyorPosition1Occupied, InputIo.NgConveyorPosition2Occupied)]
    public async Task RestartWaitsForCarrierLocationAfterStoppingBetweenSensors(InputIo destination, InputIo source)
    {
        var system = CreateSystem();
        if (destination == InputIo.NgConveyorPosition2Occupied)
            system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        await system.Signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        system.Io.SetInput(source, true);
        using var stop = new CancellationTokenSource();
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on)
            {
                system.Io.AutoResponseEnabled = false;
                system.Io.SetInput(source, false);
                stop.Cancel();
            }
        };
        await system.Conveyor.RunAsync(stop.Token);
        Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        Assert.False(system.Io.GetInput(destination));
        var stoppedShuttleDown = system.Io.GetOutput(OutputIo.NgShuttleDown);

        using var nextStop = new CancellationTokenSource();
        var restarted = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            Assert.Equal(NgConveyorState.CarrierPositionUnknown, system.Conveyor.Step);
            Assert.False(system.Conveyor.IsReceiveAllowed);
            Assert.Equal(stoppedShuttleDown, system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));

            system.Io.AutoResponseEnabled = true;
            system.Io.SetInput(destination, true);
            Assert.True(await WaitUntilAsync(
                () => system.Conveyor.IsReceiveAllowed, TimeSpan.FromSeconds(2)));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));
        }
        finally
        {
            nextStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RestartAfterInterruptedReverseWaitsForLocationThenChoosesForwardRoute()
    {
        var system = CreateSystem();
        await system.Conveyor.SetShuttleDownAsync(true);
        system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        using var stop = new CancellationTokenSource();
        system.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgConveyorRun && on && !stop.IsCancellationRequested)
            {
                system.Io.AutoResponseEnabled = false;
                system.Io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
                stop.Cancel();
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => system.Conveyor.ReturnFromConveyorAsync(stop.Token));

        using var nextStop = new CancellationTokenSource();
        var restarted = system.Conveyor.RunAsync(nextStop.Token);
        try
        {
            Assert.Equal(NgConveyorState.CarrierPositionUnknown, system.Conveyor.Step);
            Assert.False(system.Conveyor.IsReceiveAllowed);
            Assert.True(system.Io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorRun));

            system.Io.AutoResponseEnabled = true;
            system.Io.SetInput(InputIo.NgConveyorPosition2Occupied, true);
            await system.Signals.WaitForInputAsync(InputIo.NgConveyorPosition1Occupied, true, nextStop.Token);
            Assert.False(system.Io.GetOutput(OutputIo.NgConveyorReverse));
            Assert.False(system.Io.GetInput(InputIo.NgShuttleCarrierDetected));
        }
        finally
        {
            nextStop.Cancel();
            await restarted.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static TestSystem CreateSystem(int alarmCarrierCount = 3, UnitSettings? units = null, double ejectRunSeconds = 0.35)
    {
        var io = new VirtualIoService(
            Outputs(
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
                new NgCarrierTransferHardwareSettings(),
                new MachineHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        units ??= new();
        var operations = new OperationCancellation();
        var motionSettings = new InspectionGantrySettings();
        var motion = new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        var work = ConveyorStation.CreateInspection(io);
        var conveyor = new NgCarrierConveyor(io, new NgConveyorSettings { AlarmCarrierCount = alarmCarrierCount, EjectRunSeconds = ejectRunSeconds }, units);
        var pickup = new InspectionStation(
            work,
            motion,
            new MotionStatus(motion),
            conveyor,
            operations,
            motionSettings,
            new(),
            io,
            units,
            new VirtualCamera(() => motion.Position, () => []),
            new VirtualLightController(),
            new(),
            new(OpenMachineStore(), new()));
        io.Initialize();
        return new TestSystem(io, conveyor, pickup);
    }

    private static async Task LoadShuttleAsync(TestSystem system, bool lower = true)
    {
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await system.Signals.WaitForInputAsync(InputIo.NgShuttleCarrierDetected, false);
        system.Io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        if (lower)
            await system.Signals.WaitForInputAsync(InputIo.NgShuttleDown, true);
    }

    private sealed record TestSystem(
            VirtualIoService Io,
            NgCarrierConveyor Conveyor,
            InspectionStation Pickup)
    {
        public IIoService Signals => Io;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await Conveyor.RunAsync(cancellationToken);
        }
    }
}
