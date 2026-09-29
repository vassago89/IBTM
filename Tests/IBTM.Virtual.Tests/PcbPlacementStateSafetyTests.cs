using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbPlacementStateSafetyTests
{
    [Fact]
    public async Task CancelledLiftDoesNotReportMovingAxesOrLowerHandler()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        using var jogStop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var jogging = rig.Placer.JogAsync(MotionAxis.X, 1, jogStop.Token);
        try
        {
            Assert.True(rig.Motion.IsMoving);
            var lowered = false;
            rig.Io.OutputChanged += (output, value) =>
                lowered |= output == OutputIo.PcbPlacementHandlerDown && value;
            using var stop = new CancellationTokenSource();
            stop.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Placer.SetLiftDownAsync(true, stop.Token));

            Assert.False(lowered);
            await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Placer.SetLiftDownAsync(true));
        }
        finally
        {
            jogStop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jogging);
        }
    }

    [Fact]
    public async Task RepeatStepKeepsIpmRaisedWithoutDependingOnRunLoopState()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        var pressed = false;
        rig.Io.OutputChanged += (output, value) =>
            pressed |= output == OutputIo.PcbPlacementIpmDown && value;

        await rig.Placer.ExecuteStepAsync(
            PcbPlacementState.MovingToHandoff, HeatSinkSlot.HeatSink1, default, repeat: true);

        Assert.False(pressed);
        Assert.Equal(StationCylinderState.Up, rig.Placer.IpmLift);
    }

    [Fact]
    public async Task DepartureClearRemainsAvailableUntilSupplyAcknowledges()
    {
        using var rig = new PlacementRig(acknowledgeDeparture: false);
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var departing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Placer.Changed += () =>
        {
            if (rig.Placer.Handoff == PcbPlacementHandoff.Clear)
                departing.TrySetResult();
        };
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.PlacingPcb)
                stop.Cancel();
        };
        var run = rig.Placer.RunAsync(stop.Token);
        try
        {
            await departing.Task.WaitAsync(stop.Token);
            Assert.False(run.IsCompleted);
            Assert.Equal(PcbPlacementState.PreparingPlacement, rig.Placer.Phase);
            Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
            Assert.Equal(rig.Settings.HandoffPosition.X, rig.Motion.Position.X);
            Assert.True(rig.Placer.PcbSecured);
            Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);

            rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            stop.Cancel();
            await run;
        }
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
    }

    [Fact]
    public async Task StopWhileCompletingHandoffKeepsGripAndCanRestart()
    {
        using var rig = new PlacementRig(acknowledgeDeparture: false);
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var departing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Placer.Changed += () =>
        {
            if (rig.Placer.Handoff == PcbPlacementHandoff.Clear)
                departing.TrySetResult();
        };
        var run = rig.Placer.RunAsync(stop.Token);
        try
        {
            await departing.Task.WaitAsync(stop.Token);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(PcbPlacementState.PreparingPlacement, rig.Placer.Phase);
            Assert.True(rig.Placer.PcbSecured);
            Assert.False(rig.Motion.IsMoving);
            Assert.Empty(rig.Work.Assemblies);
        }
        finally
        {
            stop.Cancel();
            await run;
        }

        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, restart.Token);
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.Phase);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledRunPreservesThePhaseForReenable(bool holdingPcb)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        if (holdingPcb)
            await rig.ReceiveAsync();
        var phase = rig.Placer.Phase;
        rig.Units.PcbPlacement = false;
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, value) => commanded = true;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Placer.Trace += message =>
        {
            if (message.StartsWith("PcbPlacer: Disabled "))
            {
                Assert.Equal(PcbPlacementState.Disabled, rig.Placer.Step);
                stop.Cancel();
            }
        };

        await rig.Placer.RunAsync(stop.Token);

        Assert.False(commanded);
        Assert.Null(rig.Placer.Step);
        Assert.Equal(!holdingPcb, rig.Work.Completed);
        rig.Units.PcbPlacement = true;
        Assert.Equal(phase, rig.Placer.Phase);
        Assert.Equal(holdingPcb ? PcbPlacementState.PreparingPlacement : PcbPlacementState.MovingToHandoff,
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
    }

    [Fact]
    public async Task SelectingNextPhaseDoesNotReleaseHandoffOrStartMotion()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, value) => commanded = true;

        var step = rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1);

        Assert.Equal(PcbPlacementState.PreparingPlacement, step);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.Phase);
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Placer.ExecuteStepAsync(
            step, HeatSinkSlot.HeatSink1, stop.Token));
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.Phase);
        Assert.False(commanded);
    }

    [Fact]
    public async Task ReadingUnavailableHandoffDoesNotChangeItsCompletedPhase()
    {
        using var rig = new PlacementRig(probeFeedback: true);
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        var phase = rig.Placer.Phase;
        var changes = 0;
        rig.Placer.Changed += () => changes++;

        rig.FeedbackProbe!.OverrideState = (_, state) => state with { InPosition = false };
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        rig.FeedbackProbe.OverrideState = null;

        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
        Assert.Equal(phase, rig.Placer.Phase);
        Assert.Null(rig.Placer.Step);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task RestartInitializationPreservesConfirmedHandoff()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);

        rig.Motion.Stop();
        rig.Motion.Initialize();

        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.Phase);
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
    }

    [Fact]
    public async Task AxisAlarmInvalidatesHeldPcbHandoffUntilItsStageRunsAgain()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);

        rig.Motion.SetAlarm(MotionAxis.Y, true);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        rig.Motion.SetAlarm(MotionAxis.Y, false);
        Assert.True(MotionService.IsAt(rig.Placer.Motion.Feedback, new() { X = 50, Y = 10, Z = 12 }));
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);

        await rig.Placer.PrepareReceiptAsync();
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VacuumWithoutPcbAwayFromSupportsDoesNotRestartByMovingOrReleasing(bool repeat)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.MoveToXYAsync(new() { X = 30, Y = 20, Z = 8 });
        await rig.Placer.SetVacuumAsync(true);
        Assert.Equal(PlacementPcbState.None, rig.Placer.Pcb);
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repeat
            ? rig.Placer.RunAsync(timeout.Token, repeat: true)
            : rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.False(commanded);
        Assert.True(rig.Io.GetInput(InputIo.PcbPlacementVacuumDetected));
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(PcbPlacementState.PreparingPlacement, InputIo.PcbPlacementVacuumDetected)]
    [InlineData(PcbPlacementState.PlacingPcb, InputIo.PcbPlacementPcbDetected)]
    public async Task NormalPlacementStopsOnGripLossBeforeRelease(PcbPlacementState expected, InputIo lostInput)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        if (expected == PcbPlacementState.PlacingPcb)
        {
            await rig.Placer.ExecuteStepAsync(
                rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
            rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        }
        Assert.Equal(expected == PcbPlacementState.PreparingPlacement
            ? PcbPlacementState.WaitingForSupplyRelease : PcbPlacementState.PlacingPcb, rig.Placer.Phase);
        var lost = false;
        var lowered = false;
        var released = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMoving
                && (expected == PcbPlacementState.PreparingPlacement ? z < 11.9 : x > 50.1))
            {
                lost = true;
                rig.Io.SetInput(lostInput, false);
            }
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            lowered |= output == OutputIo.PcbPlacementHandlerDown && on;
            released |= output == OutputIo.PcbPlacementVacuumEjector && !on;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(lowered);
        Assert.False(released);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementStopsBeforeDescentWhenItsHeatSinkDisappears(bool repeat)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        if (repeat)
            rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        else
        {
            await rig.ReceiveAsync();
            await rig.Placer.ExecuteStepAsync(
                PcbPlacementState.PreparingPlacement, HeatSinkSlot.HeatSink1, default);
            rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        }
        var startingX = rig.Motion.Position.X;
        var lost = false;
        var lowered = false;
        var released = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && x > startingX + 0.1)
            {
                lost = true;
                rig.Io.SetInput(InputIo.PcbPlacementHeatSink1Present, false);
            }
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            lowered |= output == OutputIo.PcbPlacementHandlerDown && on;
            released |= output == OutputIo.PcbPlacementVacuumEjector && !on;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.ExecuteStepAsync(
            repeat ? PcbPlacementState.PickingPcb : PcbPlacementState.PlacingPcb,
            HeatSinkSlot.HeatSink1, stop.Token, repeat));

        Assert.True(lost);
        Assert.True(rig.Work.CarrierSeated);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(lowered);
        Assert.False(released);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task MachineStopDuringPreparationKeepsGripAndAllowsRestart()
    {
        var operations = new OperationCancellation();
        using var rig = new PlacementRig(operations: operations);
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        var moving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (rig.Motion.IsMoving && z < 11.9)
                moving.TrySetResult();
        };
        using var command = operations.TryBegin();
        Assert.NotNull(command);
        var preparation = rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, command.Token);
        try
        {
            await moving.Task.WaitAsync(TimeSpan.FromSeconds(2));
            operations.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            Assert.True(rig.Placer.PcbSecured);
            Assert.False(rig.Motion.IsMoving);
            Assert.Empty(rig.Work.Assemblies);
        }
        finally
        {
            command.Cancel();
            await ((Task)preparation).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        command.Dispose();
        using var restarted = operations.TryBegin();
        Assert.NotNull(restarted);
        await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, restarted.Token);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);
        Assert.True(rig.Placer.PcbSecured);
    }

    [Fact]
    public async Task ReceiptWaitsForCurrentPcbPresenceBeforeStartingVacuum()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.PrepareHandoffAsync();
        rig.Supply.Handoff = PcbSupplyHandoff.Holding;
        await rig.Placer.PrepareReceiptAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        try
        {
            await Task.Factory.StartNew(async () =>
            {
                var receiving = rig.Placer.ExecuteStepAsync(
                    PcbPlacementState.ReceivingPcb, HeatSinkSlot.HeatSink1, stop.Token);
                try
                {
                    rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
                    rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
                    await Task.Yield();
                    Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
                    Assert.False(receiving.IsCompleted);

                    rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
                    await receiving;
                    Assert.True(rig.Placer.PcbSecured);
                }
                finally
                {
                    stop.Cancel();
                    await ((Task)receiving).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing
                        | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }, CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap();
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion;
        }
    }

    [Fact]
    public async Task ReceiptStopsIfSupplyLosesHoldingDuringReceiveZ()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.PrepareHandoffAsync();
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        rig.Supply.Handoff = PcbSupplyHandoff.Holding;
        var lost = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMoving && z > 8.1)
            {
                lost = true;
                rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task HeldPcbAtReceiveWithIpmUpCanLeaveAfterSupplyRelease()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        // A stopped Repeat leaves a held PCB at receive Z with IPM Up.
        Assert.Equal(PcbPlacementHandoff.Holding, rig.Placer.Handoff);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, rig.Placer.Phase);

        rig.Io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        rig.Io.SetInput(InputIo.PcbPlacementIpmDown, false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True(await rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(rig.Placer.IsAtHorizontalZ);
        Assert.True(MotionService.IsSettled(rig.Placer.Motion.Feedback, MotionAxis.Y));
        Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
        Assert.Equal(50, rig.Motion.Position.X);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(StationCylinderState.Down, rig.Placer.IpmLift);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);
    }

    [Fact]
    public async Task CompletedPressKeepsItsResultWhenRetractionIsCancelled()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        rig.Io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        Assert.Null(rig.Placer.ActivePcb);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var interrupted = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!interrupted && rig.Work.Assemblies.Any() && z < rig.Position.Z - 0.1)
            {
                interrupted = true;
                Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.Step);
                Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.ActivePcb);
                Assert.Equal(HeatSinkSlot.HeatSink2, rig.Placer.TargetHeatSink);
                stop.Cancel();
            }
        };
        await rig.Placer.RunAsync(stop.Token);
        Assert.True(interrupted);
        Assert.Null(rig.Placer.ActivePcb);
        Assert.Single(rig.Work.Assemblies);
        Assert.Equal(PlacementPcbState.Detected, rig.Placer.Pcb);
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Motion.IsMoving);
    }

    [Fact]
    public async Task PressDoesNotRecordAnAssemblyIfPcbDisappears()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        var lost = false;
        rig.Io.InputChanged += (input, on) =>
        {
            if (input == InputIo.PcbPlacementIpmDown && on)
            {
                lost = true;
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            }
        };
        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.True(lost);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementDoesNotPressAfterCarrierChangesDuringIpmRetraction(bool replaceCarrier)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        var retracted = false;
        var pressed = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PcbPlacementIpmDown)
                return;
            pressed |= on;
            if (on)
                return;
            retracted = true;
            if (replaceCarrier)
            {
                SetCarrier(rig.Io, InputIo.PcbPlacementHeatSink1Present, false);
                SetCarrier(rig.Io, InputIo.PcbPlacementHeatSink1Present, true);
            }
            else
                rig.Io.SetInput(InputIo.PcbPlacementBackupPlateUp, false);
        };

        Assert.Equal(PcbPlacementState.PlacingPcb, rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.Contains("carrier", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(retracted);
        Assert.False(pressed);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task UnconfirmedPressDoesNotRecordAnAssembly()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        await rig.Placer.ExecuteStepAsync(rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var pressing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementIpmDown && on)
            {
                rig.Io.AutoResponseEnabled = false;
                rig.Io.SetInput(InputIo.PcbPlacementIpmUp, false);
                pressing.SetResult();
            }
        };

        var operation = rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, stop.Token);
        try
        {
            await pressing.Task.WaitAsync(stop.Token);
            Assert.False(operation.IsCompleted);
            Assert.Equal(StationCylinderState.Between, rig.Placer.IpmLift);
            Assert.Empty(rig.Work.Assemblies);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Empty(rig.Work.Assemblies);
        }
        finally
        {
            stop.Cancel();
            await ((Task)operation).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class PlacementRig : IDisposable
    {
        public PlacementRig(bool probeFeedback = false, OperationCancellation? operations = null, bool acknowledgeDeparture = true)
        {
            AcknowledgeDeparture = acknowledgeDeparture;
            Settings = new PcbPlacementHandlerSettings
            {
                Motion = new() { HorizontalSpeed = 200, ZSpeed = 50 },
                HandoffPosition = new() { X = 50, Y = 10, Z = 8 },
                ReceiveZ = 12,
            };
            Position = new() { X = 70, Y = 20, Z = 10 };
            Io = new(Outputs(new PcbPlacementHandlerHardwareSettings(), new ConveyorHardwareSettings()), new());
            Motion = new(Settings.Motion, operations ?? new());

            Supply = new() { Handoff = PcbSupplyHandoff.Released };
            Units = new();
            Work = ConveyorStation.CreatePcbPlacement(Io);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = Position;
            IMotionFeedback feedback = Motion;
            if (probeFeedback)
            {
                feedback = System.Reflection.DispatchProxy.Create<IXyMotion, MachineLifecycleTests.ScopedMotionProbe>();
                FeedbackProbe = (MachineLifecycleTests.ScopedMotionProbe)feedback;
                FeedbackProbe.Motion = Motion;
                FeedbackProbe.ReportReady = true;
            }
            Placer = new PcbPlacer((IXyMotion)feedback, new MotionStatus(feedback),
                Io,
                Settings,
                Supply,
                Work,
                recipes,
                Units);
            Io.OutputChanged += OnOutputChanged;
            Placer.Changed += OnPlacementChanged;
        }

        private bool AcknowledgeDeparture { get; }
        public AxisPosition Position { get; }
        public UnitSettings Units { get; }
        public PcbPlacementHandlerSettings Settings { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public ConveyorStation Work { get; }
        public PcbPlacer Placer { get; }
        public MachineLifecycleTests.ScopedMotionProbe? FeedbackProbe { get; }
        public SupplyFeedback Supply { get; }

        public async Task ReceiveAsync()
        {
            await Placer.PrepareHandoffAsync();
            Supply.Handoff = PcbSupplyHandoff.Holding;
            Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await Placer.ExecuteStepAsync(Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
            Supply.Handoff = PcbSupplyHandoff.Released;
        }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            Io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await Work.SeatAsync(CancellationToken.None);
            await Placer.SetLiftDownAsync(false);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
            await Placer.MoveAxisAsync(MotionAxis.Z, Settings.HandoffPosition.Z);
        }

        private void OnOutputChanged(OutputIo output, bool on)
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                Io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        }

        private void OnPlacementChanged()
        {
            if (AcknowledgeDeparture && Supply.Handoff == PcbSupplyHandoff.Released
                && Placer.Handoff == PcbPlacementHandoff.Clear)
                Supply.Handoff = PcbSupplyHandoff.Unavailable;
        }

        public void Dispose()
        {
            Motion.Dispose();
        }
    }

    private sealed class SupplyFeedback : IPcbSupplyHandoff
    {
        public event Action? Changed;
        public bool PcbSecured => Handoff == PcbSupplyHandoff.Holding;
        public PcbSupplyHandoff Handoff
        {
            get;
            set
            {
                field = value;
                Changed?.Invoke();
            }
        }
    }
}
