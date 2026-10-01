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
using static IBTM.Virtual.Tests.VirtualTestSupport;

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
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.ReceivingPcb)
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            if (rig.Placer.Step is PcbPlacementState.WaitingForSupplyRelease)
                rig.Supply.Handoff = PcbSupplyHandoff.Released;
        };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartReachesStandbyAtHeatSinkYWithoutWaitingForSupply(bool repeat)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.MoveAxisAsync(MotionAxis.Y, rig.Settings.HandoffPosition.Y);
        await rig.Placer.MoveAxisAsync(MotionAxis.Z, rig.Settings.ReceiveZ!.Value);
        rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        var horizontalBeforeStandbyZ = false;
        rig.Motion.PositionChanged += (x, y, z) =>
            horizontalBeforeStandbyZ |= (x != 0 || y != rig.Settings.HandoffPosition.Y)
                && z != rig.Settings.HandoffPosition.Z;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var reachedStandby = false;
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState step && step == (repeat
                ? PcbPlacementState.WaitingForCarrier : PcbPlacementState.MovingToHandoff))
            {
                reachedStandby = true;
                stop.Cancel();
            }
        };

        await rig.Placer.RunAsync(stop.Token, repeat);

        Assert.True(reachedStandby);
        Assert.False(horizontalBeforeStandbyZ);
        Assert.Equal(rig.Settings.HandoffPosition.Z, rig.Motion.Position.Z);
        Assert.Equal(rig.Settings.HandoffPosition.X, rig.Motion.Position.X);
        Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
        Assert.Equal(StationCylinderState.Up, rig.Placer.Lift);
        Assert.Equal(StationCylinderState.Up, rig.Placer.IpmLift);
        Assert.False(rig.Work.Completed);
        Assert.Empty(rig.Work.Assemblies);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlacementCompletesOnlyAfterReturningXAtStandbyZ(bool cancelReturn)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await rig.Placer.ExecuteStepAsync(
            PcbPlacementState.PreparingPlacement, HeatSinkSlot.HeatSink1, stop.Token);
        await rig.Placer.ExecuteStepAsync(
            PcbPlacementState.PlacingPcb, HeatSinkSlot.HeatSink1, stop.Token);
        Assert.Single(rig.Work.Assemblies);
        Assert.False(rig.Work.Completed);
        Assert.Equal(PcbPlacementState.Retracting, rig.Placer.Phase);
        var returningX = false;
        var completedBeforeArrival = false;
        rig.Work.Changed += () => completedBeforeArrival |= rig.Work.Completed
            && (rig.Motion.IsMoving || rig.Motion.Position.X != rig.Settings.HandoffPosition.X
                || rig.Motion.Position.Z != rig.Settings.HandoffPosition.Z);
        rig.Motion.MovingChanged += moving =>
        {
            if (moving && rig.Motion.Position.Z == rig.Settings.HandoffPosition.Z
                && rig.Motion.Position.X != rig.Settings.HandoffPosition.X)
            {
                returningX = true;
                Assert.False(rig.Work.Completed);
                Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
                if (cancelReturn)
                    stop.Cancel();
            }
        };

        var retracting = rig.Placer.ExecuteStepAsync(PcbPlacementState.Retracting, null, stop.Token);
        if (cancelReturn)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retracting);
        else
            await retracting;

        Assert.True(returningX);
        Assert.False(completedBeforeArrival);
        Assert.Equal(!cancelReturn, rig.Work.Completed);
        Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
        Assert.Equal(rig.Settings.HandoffPosition.Z, rig.Motion.Position.Z);
        if (!cancelReturn)
            Assert.Equal(rig.Settings.HandoffPosition.X, rig.Motion.Position.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CarrierRemovalAndReplacementStopsPlacementWithoutResettingItsWork(bool duringRetraction)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        rig.Supply.Handoff = duringRetraction ? PcbSupplyHandoff.Holding : PcbSupplyHandoff.Unavailable;
        var job = rig.Work.CurrentJob;
        var changed = false;
        void ReplaceCarrier()
        {
            changed = true;
            rig.Io.SetInput(InputIo.PcbPlacementHeatSink1Present, false);
            rig.Io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        }
        rig.Placer.Trace += message =>
        {
            if (!duringRetraction && !changed && message.StartsWith("Waiting for feedback / work change:"))
                ReplaceCarrier();
        };
        rig.Placer.StepChanged += () =>
        {
            switch (rig.Placer.Step)
            {
                case PcbPlacementState.ReceivingPcb:
                    rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
                    break;
                case PcbPlacementState.WaitingForSupplyRelease:
                    rig.Supply.Handoff = PcbSupplyHandoff.Released;
                    break;
                case PcbPlacementState.Retracting when duringRetraction && !changed && rig.Work.Assemblies.Any():
                    ReplaceCarrier();
                    break;
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Placer.RunAsync(stop.Token));

        Assert.True(changed);
        Assert.False(stop.IsCancellationRequested);
        Assert.True(rig.Work.CarrierSeated);
        Assert.Same(job, rig.Work.CurrentJob);
        Assert.Equal(duringRetraction ? 1 : 0, rig.Work.Assemblies.Count());
        Assert.False(rig.Work.Completed);
        Assert.False(rig.Motion.IsMoving);
        Assert.False(rig.Placer.IsRunning);
    }

    [Fact]
    public async Task NewRunRequiresManualRemovalAndStartsAtFirstTarget()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.ReceiveAsync();
        var assembly = rig.Work.GetAssembly(HeatSinkSlot.HeatSink1);
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, value) => commanded = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.RunAsync());
        Assert.False(commanded);
        Assert.True(rig.Placer.PcbSecured);

        await rig.Placer.SetVacuumAsync(false);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.MovingToHandoff)
                stop.Cancel();
        };
        await rig.Placer.RunAsync(stop.Token);
        Assert.Equal(PcbPlacementState.MovingToHandoff, rig.Placer.Phase);
        Assert.Equal(HeatSinkSlot.HeatSink1, rig.Placer.TargetHeatSink);
        Assert.Same(assembly, Assert.Single(rig.Work.Assemblies));
        Assert.False(rig.Work.Completed);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledRunClearsThePreviousHandoff(bool holdingPcb)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        if (holdingPcb)
            await rig.ReceiveAsync();
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
        Assert.Equal(PcbPlacementState.MovingToHandoff, rig.Placer.Phase);
        Assert.Equal(holdingPcb ? PcbPlacementState.MovingToHandoff : PcbPlacementState.WaitingForCarrier,
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
    public async Task ReceiptDoesNotCompleteWhenPcbDetectionDropsDuringVacuumPickup()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.PrepareHandoffAsync();
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector && on)
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, false);
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.ExecuteStepAsync(
            PcbPlacementState.ReceivingPcb, HeatSinkSlot.HeatSink1, stop.Token));

        Assert.Equal(PcbPlacementState.ReceivingPcb, rig.Placer.Phase);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        Assert.Equal(rig.Settings.ReceiveZ, rig.Motion.Position.Z);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Fact]
    public async Task ReturnReleaseRejectsMovementAwayFromReceivePositionWhileStopped()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        rig.Supply.Handoff = PcbSupplyHandoff.Released;
        await rig.Placer.PrepareHandoffAsync(returning: true);
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await rig.Placer.SetVacuumAsync(true);
        await rig.Placer.PrepareReceiptAsync(returning: true);
        Assert.Equal(PcbPlacementHandoff.Returning, rig.Placer.Handoff);
        await rig.Motion.MoveAxisAsync(MotionAxis.X, 55, 2_000);
        rig.Supply.Handoff = PcbSupplyHandoff.Holding;
        var released = false;
        rig.Io.OutputChanged += (output, on) =>
            released |= output == OutputIo.PcbPlacementVacuumEjector && !on;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, stop.Token, repeat: true));

        Assert.False(released);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(55, rig.Motion.Position.X);
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
        Assert.True(MotionServiceBase.IsAt(rig.Placer.Motion.Feedback, new() { X = 50, Y = 10, Z = 12 }));
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
    public async Task MachineStopDuringPreparationKeepsGripAndBlocksRestart()
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Placer.RunAsync());
        Assert.True(rig.Placer.PcbSecured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffApproachStopsIfSupplyReadinessIsLost(bool returning)
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        rig.Supply.Handoff = returning ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Holding;
        var lost = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMovingHorizontal && x > 1)
            {
                lost = true;
                rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Placer.PrepareHandoffAsync(timeout.Token, returning));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.Equal(0, rig.Motion.Position.Y);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnDescentRequiresSupplyReadinessBeforeAndDuringMovement(bool loseDuringDescent)
    {
        using var rig = new PlacementRig(acknowledgeDeparture: false);
        await rig.InitializeAsync();
        rig.Supply.Handoff = PcbSupplyHandoff.Released;
        rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await rig.Placer.SetVacuumAsync(true);
        await rig.Placer.PrepareHandoffAsync(returning: true);
        var lost = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (loseDuringDescent && !lost && z > 8.1)
            {
                lost = true;
                rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
            }
        };
        if (!loseDuringDescent)
        {
            using var stopped = new CancellationTokenSource();
            stopped.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.Placer.PrepareReceiptAsync(stopped.Token, returning: true));
            Assert.Equal(PcbPlacementState.PresentingToSupply, rig.Placer.Phase);
            rig.Supply.Handoff = PcbSupplyHandoff.Unavailable;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Placer.ExecuteStepAsync(
            PcbPlacementState.PresentingToSupply, HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.Equal(loseDuringDescent, lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.True(rig.Motion.Position.Z < rig.Settings.ReceiveZ);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
        Assert.Empty(rig.Work.Assemblies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptDoesNotDescendAfterHandoffXyWasMovedWhileStopped(bool returning)
    {
        using var rig = new PlacementRig(acknowledgeDeparture: false);
        await rig.InitializeAsync();
        if (returning)
        {
            rig.Supply.Handoff = PcbSupplyHandoff.Released;
            rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await rig.Placer.SetVacuumAsync(true);
        }
        await rig.Placer.PrepareHandoffAsync(returning: returning);
        await rig.Placer.MoveAxisAsync(MotionAxis.X, rig.Settings.HandoffPosition.X + 10);
        var position = rig.Motion.Position;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Placer.ExecuteStepAsync(
            rig.Placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.Equal(position, rig.Motion.Position);
        Assert.False(rig.Motion.IsMoving);
        Assert.Equal(PcbPlacementHandoff.Unavailable, rig.Placer.Handoff);
    }

    [Fact]
    public async Task ReceiptWaitsForCurrentPcbPresenceBeforeStartingVacuum()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        await rig.Placer.PrepareHandoffAsync();
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
        Assert.True(MotionServiceBase.IsSettled(rig.Placer.Motion.Feedback, MotionAxis.Y));
        Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
        Assert.Equal(50, rig.Motion.Position.X);
        Assert.True(rig.Placer.PcbSecured);
        Assert.Equal(StationCylinderState.Down, rig.Placer.IpmLift);
        Assert.Equal(PcbPlacementHandoff.Clear, rig.Placer.Handoff);
    }

    [Fact]
    public async Task CompletedCarrierIsNotPlacedAgainOnStart()
    {
        using var rig = new PlacementRig();
        await rig.InitializeAsync();
        var job = rig.Work.CurrentJob;
        var assembly = rig.Work.GetAssembly(HeatSinkSlot.HeatSink1);
        rig.Work.Complete(job);
        var handledPcb = false;
        rig.Io.OutputChanged += (output, on) => handledPcb |= on
            && output is OutputIo.PcbPlacementHandlerDown or OutputIo.PcbPlacementVacuumEjector;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.WaitingForCarrier)
                stop.Cancel();
        };
        await rig.Placer.RunAsync(stop.Token);
        Assert.False(handledPcb);
        Assert.Equal(rig.Settings.HandoffPosition.X, rig.Motion.Position.X);
        Assert.Equal(rig.Settings.HandoffPosition.Z, rig.Motion.Position.Z);
        Assert.Equal(rig.Position.Y, rig.Motion.Position.Y);
        Assert.True(rig.Work.Completed);
        Assert.Same(job, rig.Work.CurrentJob);
        Assert.Same(assembly, Assert.Single(rig.Work.Assemblies));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VacuumTimeoutBeforeLiftKeepsPcbSupportedForMaintenance(bool repeat)
    {
        using var rig = new PlacementRig(vacuumTimeout: 300);
        await rig.InitializeAsync();
        rig.Placer.StepChanged += () =>
        {
            if (rig.Placer.Step is PcbPlacementState.ReceivingPcb or PcbPlacementState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector && on)
                rig.Io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = rig.Placer.RunAsync(stop.Token, repeat);
        var failure = await Assert.ThrowsAsync<MaintenanceStopException>(() => run);
        Assert.IsType<IoTimeoutException>(failure.InnerException);
        Assert.Equal(repeat ? rig.Position.Z : rig.Settings.ReceiveZ, rig.Motion.Position.Z);
        Assert.Equal(PcbSupplyHandoff.Holding, rig.Supply.Handoff);
        Assert.False(rig.Work.Completed);
        Assert.Empty(rig.Work.Assemblies);
        Assert.False(rig.Motion.IsMoving);
    }

    private sealed class PlacementRig : IDisposable
    {
        public PlacementRig(bool probeFeedback = false, OperationCancellation? operations = null, bool acknowledgeDeparture = true, int vacuumTimeout = 10_000)
        {
            AcknowledgeDeparture = acknowledgeDeparture;
            Settings = new PcbPlacementHandlerSettings
            {
                Motion = new() { HorizontalSpeed = 200, ZSpeed = 50 },
                HandoffPosition = new() { X = 50, Y = 10, Z = 8 },
                ReceiveZ = 12,
            };
            Position = new() { X = 70, Y = 20, Z = 10 };
            Io = new(Outputs(new PcbPlacementHandlerHardwareSettings(), new ConveyorHardwareSettings()), new() { TimeoutMilliseconds = vacuumTimeout });
            Motion = new(Settings.Motion, operations ?? new());

            Supply = new() { Handoff = PcbSupplyHandoff.Holding };
            Units = new();
            Work = ConveyorStation.CreatePcbPlacement(Io);
            var recipes = new RecipeManager(OpenMachineStore(), new());
            recipes.Current.PcbPlacement.HeatSink1PcbPlacementPosition = Position;
            IMotionFeedback feedback = Motion;
            if (probeFeedback)
            {
                feedback = System.Reflection.DispatchProxy.Create<IXyMotion, MachineTestSupport.ScopedMotionProbe>();
                FeedbackProbe = (MachineTestSupport.ScopedMotionProbe)feedback;
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
        public MachineTestSupport.ScopedMotionProbe? FeedbackProbe { get; }
        public SupplyFeedback Supply { get; }

        public async Task ReceiveAsync()
        {
            Supply.Handoff = PcbSupplyHandoff.Holding;
            await Placer.PrepareHandoffAsync();
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
