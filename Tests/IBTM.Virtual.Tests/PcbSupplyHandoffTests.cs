using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class PcbSupplyHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandbyPreparesGripRotatesAtHandoffAndWaitsAbovePcbOne(bool alreadyRotated)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        if (alreadyRotated)
            await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true);
        await rig.Motion.MoveAxisAsync(MotionAxis.Z, 15, 2_000);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        var rotated = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyRotate && on)
            {
                rotated = true;
                Assert.Equal((50d, 10d, 7d), rig.Motion.Position);
            }
        };
        var previous = rig.Motion.Position;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            Assert.True(rig.Supplier.PcbReleased);
            if (previous.X != x || previous.Y != y)
                Assert.Equal(3, z);
            previous = (x, y, z);
        };

        await rig.Supplier.MoveToStandbyAsync();

        Assert.Equal(!alreadyRotated, rotated);
        Assert.Equal((10d, 10d, 3d), rig.Motion.Position);
        Assert.True(rig.Supplier.PcbReleased);
        Assert.Equal(PcbSupplyRotationState.Rotated, rig.Supplier.Rotation);
    }


    [Fact]
    public async Task HandoffReturnRotatesBeforeAnyAxisMoves()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        var rotated = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyRotate && on)
            {
                Assert.Equal((50d, 10d, 7d), rig.Motion.Position);
                rotated = true;
            }
        };
        rig.Motion.MovingChanged += moving =>
        {
            if (moving)
                Assert.True(rotated);
        };
        var previous = rig.Motion.Position;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (previous.X != x || previous.Y != y)
                Assert.Equal(rig.Settings.TravelZ, z);
            previous = (x, y, z);
        };

        await rig.Supplier.MoveFromHandoffAsync(rig.Recipes.Current.PcbSupply.Pcb2PickPosition);

        Assert.True(rotated);
        Assert.Equal(PcbSupplyRotationState.Rotated, rig.Supplier.Rotation);
        Assert.Equal((20d, 10d, 3d), rig.Motion.Position);
    }

    [Fact]
    public async Task HandoffMoveDoesNotSkipSmallPositionError()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        var target = rig.Settings.HandoffPosition;
        await rig.Motion.AdjustAxisAsync(MotionAxis.X, target.X + 0.04, 2_000);

        await rig.Supplier.PrepareHandoffAsync(CancellationToken.None);

        Assert.Equal((target.X, target.Y, target.Z), rig.Motion.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupChecksPcbOnlyAfterLiftingToTravelZ(bool detectedAtTravelZ)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        var pick = rig.Recipes.Current.PcbSupply.Pcb1PickPosition;
        var gripped = false;
        var raised = false;
        var completed = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && on)
            {
                Assert.Equal((pick.X, pick.Y!.Value, pick.Z), rig.Motion.Position);
                Assert.Equal(PcbSupplyCylinderState.Backward, rig.Supplier.Gripper);
            }
            if (output == OutputIo.PcbSupplyGripperClosed && on)
            {
                Assert.Equal((pick.X, pick.Y!.Value, pick.Z), rig.Motion.Position);
                Assert.True(rig.Io.GetInput(InputIo.PcbSupplyIpmFixerForward));
                Assert.Equal(!detectedAtTravelZ, rig.Io.GetInput(InputIo.PcbSupplyPcbDetected));
                gripped = true;
            }
        };
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!gripped && x == pick.X && y == pick.Y && z == pick.Z)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, !detectedAtTravelZ);
            if (gripped && !raised && z == rig.Settings.TravelZ)
            {
                Assert.Equal(PcbSupplyCylinderState.Forward, rig.Supplier.Gripper);
                Assert.True(rig.Io.GetInput(InputIo.PcbSupplyIpmFixerForward));
                raised = true;
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, detectedAtTravelZ);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        rig.Supplier.StepChanged += () =>
        {
            if (raised && rig.Supplier.Step is PcbSupplyState.HandingOff or PcbSupplyState.WaitingForCarrier)
            {
                completed = true;
                stop.Cancel();
            }
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.True(gripped);
        Assert.True(raised);
        Assert.True(completed);
        Assert.Equal(detectedAtTravelZ, rig.Supplier.PcbSecured);
        Assert.Equal(!detectedAtTravelZ, rig.Supplier.PcbReleased);
        Assert.Equal(detectedAtTravelZ ? PcbSupplyState.HandingOff : PcbSupplyState.WaitingForCarrier,
            rig.Supplier.Phase);
    }

    [Theory]
    [InlineData(InputIo.PcbSupplyGripperClosed)]
    [InlineData(InputIo.PcbSupplyIpmFixerForward)]
    public async Task PickupLiftStopsOnGripLossBeforePcbDetection(InputIo lostInput)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Settings.Motion.ZSpeed = 50;
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        var pick = rig.Recipes.Current.PcbSupply.Pcb1PickPosition;
        var gripping = false;
        var lost = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && on)
                gripping = true;
        };
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (gripping && !lost && z < pick.Z && z > rig.Settings.TravelZ)
            {
                lost = true;
                rig.Io.SetInput(lostInput, false);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Supplier.RunAsync(rig.Placement, stop.Token));

        Assert.Contains("gripper or IPM fixation", failure.Message);
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.True(rig.Motion.Position.Z > rig.Settings.TravelZ);
        Assert.Equal((pick.X, pick.Y!.Value), (rig.Motion.Position.X, rig.Motion.Position.Y));
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.False(rig.Io.GetInput(InputIo.PcbSupplyPcbDetected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupStopsWhenRotationFeedbackIsLost(bool duringDescent)
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        rig.Settings.Motion.ZSpeed = 50;
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Recipes.Current.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 };
        rig.Recipes.Current.PcbSupply.Pcb2PickPosition = new() { X = 20, Y = 10, Z = 8 };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var rotationLost = false;
        void LoseRotation()
        {
            rotationLost = true;
            rig.Io.SetInputs((InputIo.PcbSupplyRotated, false), (InputIo.PcbSupplyUnrotated, true));
        }
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
            {
                if (!duringDescent && !rotationLost)
                    LoseRotation();
                rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            }
        };
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (duringDescent && !rotationLost && x == 10 && z > rig.Settings.TravelZ + 0.1)
                LoseRotation();
            if (rotationLost && z >= 8)
                stop.Cancel();
        };
        var failure = await Assert.ThrowsAsync<MotionInterlockException>(() => rig.Supplier.RunAsync(rig.Placement, stop.Token));

        Assert.Contains("rotation", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(rotationLost);
        Assert.False(rig.Motion.IsMoving);
        Assert.True(rig.Motion.Position.Z < 8);
    }

    [Fact]
    public async Task CarrierLeavingDuringSecondPickupResetsTheNextCarrierToPcbOne()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 8 },
        };
        rig.Recipes.Current.PcbSupply = recipe;
        var departed = false;
        var replacement = false;
        var approachingReplacement = false;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picked = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        rig.Supplier.StepChanged += () =>
        {
            var position = rig.Motion.Position;
            if (!departed && rig.Supplier.Step is PcbSupplyState.PickingPcb
                && position.X == 20 && position.Z == 8)
            {
                departed = true;
                rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            }
            if (departed && !replacement && rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
                waiting.TrySetResult();
            if (replacement && rig.Supplier.Step is PcbSupplyState.MovingToPickup)
                approachingReplacement = true;
            if (approachingReplacement && rig.Supplier.Step is PcbSupplyState.PickingPcb)
            {
                picked.TrySetResult(position.X);
                stop.Cancel();
            }
        };
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            replacement = true;
            rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            Assert.Equal(recipe.Pcb1PickPosition.X, await picked.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task RestartBeginsWithPcbOne()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        rig.Recipes.Current.PcbSupply = new()
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 8 },
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.WaitingForCarrierExit)
                stop.Cancel();
        };
        await rig.Supplier.RunAsync(rig.Placement, stop.Token);
        Assert.Equal(PcbSupplyState.WaitingForCarrierExit, rig.Supplier.Phase);
        using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        double? pickedX = null;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
            {
                pickedX = rig.Motion.Position.X;
                restart.Cancel();
            }
        };
        await rig.Supplier.RunAsync(rig.Placement, restart.Token);
        Assert.Equal(10, pickedX);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpstreamDepartureDuringGrippingDoesNotResetToEmptyPickup(bool afterGrip)
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        var departed = false;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (afterGrip && !departed && rig.Supplier.Step is PcbSupplyState.MovingToHandoff)
            {
                departed = true;
                rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            }
        };
        rig.Recipes.Current.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 };
        rig.Io.InputChanged += (input, on) =>
        {
            if (!afterGrip && !departed && input == InputIo.PcbSupplyIpmFixerForward && on)
            {
                departed = true;
                rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            }
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Supplier.RunAsync(rig.Placement, stop.Token));

        Assert.True(departed);
        Assert.Equal(afterGrip, rig.Supplier.PcbSecured);
        Assert.Equal(afterGrip, rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.False(rig.Motion.IsMoving);
        Assert.Equal((10d, 10d, afterGrip ? rig.Settings.TravelZ : 8d), rig.Motion.Position);
        Assert.NotEqual(PcbSupplyState.WaitingForCarrier, rig.Supplier.Phase);
    }

    [Fact]
    public async Task DisabledRunClearsThePreviousHandoff()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Units.PcbSupply = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        rig.Supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: Disabled "))
            {
                Assert.Equal(PcbSupplyState.Disabled, rig.Supplier.Step);
                stop.Cancel();
            }
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.Null(rig.Supplier.Step);
        rig.Units.PcbSupply = true;
        Assert.Equal(PcbSupplyState.MovingToPickup, rig.Supplier.Phase);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NewRunAllowsVisiblePcbWithReleasedGripper(bool facingPickup, bool fixerForward)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await rig.Supplier.SetTeachingRotationAsync(facingPickup);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, fixerForward);
        rig.Motion.MovingChanged += moving =>
        {
            if (moving)
                Assert.True(rig.Supplier.PcbReleased);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waitingForCarrier = false;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
            {
                waitingForCarrier = true;
                stop.Cancel();
            }
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.True(waitingForCarrier);
        Assert.True(rig.Supplier.PcbReleased);
        Assert.Equal(PcbSupplyPcbState.Detected, rig.Supplier.Pcb);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.False(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
    }

    [Fact]
    public async Task NewRunPreparesEmptyClosedGripperBeforeMoving()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        var opened = false;
        var moved = false;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyGripperClosed && !on)
            {
                Assert.False(rig.Io.GetInput(InputIo.PcbSupplyIpmFixerForward));
                opened = true;
            }
        };
        rig.Motion.MovingChanged += moving =>
        {
            if (!moving)
                return;
            Assert.True(opened);
            Assert.True(rig.Supplier.PcbReleased);
            moved = true;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
                stop.Cancel();
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.True(moved);
        Assert.True(rig.Supplier.PcbReleased);
        Assert.False(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.False(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
    }

    [Theory]
    [InlineData(OutputIo.PcbSupplyIpmFixerForward)]
    [InlineData(OutputIo.PcbSupplyGripperClosed)]
    public async Task NewRunDoesNotMoveWithoutStartupCylinderFeedback(OutputIo stalledOutput)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        // Preserve the open-gripper/visible-PCB case for fixer retraction.
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, stalledOutput == OutputIo.PcbSupplyIpmFixerForward);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(stalledOutput, true);
        rig.Io.AutoResponseEnabled = false;
        var moved = false;
        rig.Motion.MovingChanged += moving => moved |= moving;

        var error = await Assert.ThrowsAsync<IoTimeoutException>(() => rig.Supplier.RunAsync(rig.Placement));

        Assert.False(moved);
        Assert.False(rig.Io.GetOutput(stalledOutput));
        Assert.Equal(stalledOutput == OutputIo.PcbSupplyIpmFixerForward
            ? InputIo.PcbSupplyIpmFixerForward : InputIo.PcbSupplyGripperOpen, error.Input);
        Assert.False(rig.Supplier.PcbReleased);
    }

    [Fact]
    public async Task PcbDetectedDuringStartupStopsBeforeOpeningClosedGripper()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        var detected = false;
        var moved = false;
        rig.Motion.MovingChanged += moving => moved |= moving;
        rig.Io.InputChanged += (input, on) =>
        {
            if (input == InputIo.PcbSupplyIpmFixerForward && !on)
            {
                detected = true;
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, false);
            }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supplier.RunAsync(rig.Placement));

        Assert.Contains("startup", error.Message);
        Assert.True(detected);
        Assert.False(moved);
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.Equal(PcbSupplyCylinderState.Forward, rig.Supplier.Gripper);
    }

    [Fact]
    public async Task StopDuringStartupDoesNotOpenGripperOrMove()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        using var stop = new CancellationTokenSource();
        var moved = false;
        rig.Motion.MovingChanged += moving => moved |= moving;
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                stop.Cancel();
        };

        await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.True(stop.IsCancellationRequested);
        Assert.False(moved);
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
    }

    [Fact]
    public async Task NewRunRejectsHeldPcbWithoutCompletedHandoff()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        await rig.Supplier.MoveAxisAsync(MotionAxis.X, rig.Settings.HandoffPosition.X + 10);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.False(rig.Supplier.IsHandoffRestartAllowed);
        var commanded = false;
        rig.Motion.MovingChanged += moving => commanded |= moving;
        rig.Io.OutputChanged += (output, on) => commanded = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supplier.RunAsync(rig.Placement));
        Assert.False(commanded);
        Assert.True(rig.Supplier.PcbSecured);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RestartKeepsHandoffPositionOrReturnsAfterManualMovementThenStartsPickupFromPcbOne(int stoppedPcb)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var restarting = false;
        var handoffs = 0;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb && !restarting)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (rig.Supplier.Step is PcbSupplyState.HandingOff && !restarting)
            {
                if (++handoffs == stoppedPcb)
                    stop.Cancel();
                else
                    rig.Placement.Handoff = PcbPlacementHandoff.Holding;
            }
            if (rig.Supplier.Step is PcbSupplyState.WaitingForPlacementClear)
            {
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, false);
                rig.Placement.Handoff = PcbPlacementHandoff.Clear;
            }
            if (rig.Supplier.Step is PcbSupplyState.MovingToPickup)
                rig.Placement.Handoff = PcbPlacementHandoff.Unavailable;
        };
        await rig.Supplier.RunAsync(rig.Placement, stop.Token);
        Assert.Equal(stoppedPcb, handoffs);
        // Stopped manual movement must not require a position snapshot to admit the next START.
        if (stoppedPcb == 2)
            await rig.Supplier.MoveAxisAsync(MotionAxis.X, rig.Settings.HandoffPosition.X + 10);
        Assert.True(rig.Supplier.IsHandoffRestartAllowed);
        Assert.Equal(PcbSupplyHandoff.Holding, rig.Supplier.Handoff);

        restarting = true;
        var moved = false;
        var changedGripOrRotation = false;
        rig.Motion.MovingChanged += moving =>
        {
            moved |= moving;
            if (moving)
                Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        };
        rig.Io.OutputChanged += (output, on) => changedGripOrRotation |= output
            is OutputIo.PcbSupplyRotate or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Supplier.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback") && rig.Supplier.Phase == PcbSupplyState.HandingOff)
                waiting.TrySetResult();
        };
        using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var returnedToPcbOne = false;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.WaitingForCarrier)
            {
                returnedToPcbOne = true;
                restart.Cancel();
            }
        };
        var run = rig.Supplier.RunAsync(rig.Placement, restart.Token);
        try
        {
            await waiting.Task.WaitAsync(restart.Token);
            Assert.Equal(stoppedPcb == 2, moved);
            Assert.Equal(
                (rig.Settings.HandoffPosition.X, rig.Settings.HandoffPosition.Y, rig.Settings.HandoffPosition.Z),
                rig.Motion.Position);
            Assert.False(changedGripOrRotation);
            Assert.True(rig.Supplier.PcbSecured);
            rig.Placement.Handoff = PcbPlacementHandoff.Holding;
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(returnedToPcbOne);
            Assert.Equal(rig.Recipes.Current.PcbSupply.Pcb1PickPosition.X, rig.Motion.Position.X);
            Assert.Equal(rig.Settings.TravelZ, rig.Motion.Position.Z);
            Assert.True(rig.Supplier.PcbReleased);
        }
        finally
        {
            restart.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ReadingUnavailableHandoffDoesNotChangeItsCompletedPhase()
    {
        using var rig = new HandoffRig(probeFeedback: true);
        await rig.InitializeAsync();
        var phase = rig.Supplier.Phase;
        var changes = 0;
        rig.Supplier.Changed += () => changes++;

        // A Watch evaluation observes current feedback; it cannot commit a transition.
        rig.FeedbackProbe!.OverrideState = (_, state) => state with { InPosition = false };
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.FeedbackProbe.OverrideState = null;

        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);
        Assert.Equal(phase, rig.Supplier.Phase);
        Assert.Null(rig.Supplier.Step);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task RestartInitializationPreservesConfirmedHandoff()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.Equal(PcbSupplyHandoff.Holding, rig.Supplier.Handoff);

        rig.Motion.Stop();
        rig.Motion.Initialize();

        Assert.Equal(PcbSupplyState.HandingOff, rig.Supplier.Phase);
        Assert.Equal(PcbSupplyHandoff.Holding, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task HandoffReflectsCurrentServoFeedback()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);

        rig.Motion.SetServo(MotionAxis.X, false);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.Motion.SetServo(MotionAxis.X, true);
        Assert.True(VirtualTestSupport.IsAt(rig.Supplier.Motion.Feedback, rig.Settings.HandoffPosition));
        Assert.Equal(PcbSupplyHandoff.Released, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task ColdStartResumesConfirmedHandoffWithoutMovingOrReleasingEarly()
    {
        using var rig = new HandoffRig();
        rig.Io.Initialize();
        rig.Motion.Initialize();
        await HomeAsync(rig.Motion, 2_000);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        var target = rig.Settings.HandoffPosition;
        await rig.Motion.MoveToXYAsync(target.X, target.Y, rig.Settings.Motion.HorizontalSpeed);
        await rig.Motion.MoveAxisAsync(MotionAxis.Z, target.Z, rig.Settings.Motion.ZSpeed);

        Assert.True(VirtualTestSupport.IsAt(rig.Supplier.Motion.Feedback, rig.Settings.HandoffPosition));
        Assert.True(rig.Supplier.PcbSecured);
        Assert.Equal(PcbSupplyState.MovingToPickup, rig.Supplier.Phase);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.True(rig.Supplier.IsHandoffRestartAllowed);

        var moved = false;
        var changedGripOrRotation = false;
        rig.Motion.MovingChanged += moving => moved |= moving;
        rig.Io.OutputChanged += (output, on) => changedGripOrRotation |= output
            is OutputIo.PcbSupplyRotate or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Supplier.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback") && rig.Supplier.Phase == PcbSupplyState.HandingOff)
                waiting.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token);
        try
        {
            await waiting.Task.WaitAsync(stop.Token);
            Assert.False(moved);
            Assert.False(changedGripOrRotation);
            Assert.Equal(PcbSupplyHandoff.Holding, rig.Supplier.Handoff);
            rig.Placement.Handoff = PcbPlacementHandoff.Holding;
            await WaitUntilAsync(() => rig.Supplier.Phase == PcbSupplyState.WaitingForPlacementClear);
            Assert.True(rig.Supplier.PcbReleased);
            Assert.False(moved);
            Assert.Equal((target.X, target.Y, target.Z), rig.Motion.Position);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRestartDoesNotOfferPcbToPlacement(bool loseGrip)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        // A return is needed only when the stopped operator moved away from handoff.
        await rig.Supplier.MoveAxisAsync(MotionAxis.X, rig.Settings.HandoffPosition.X + 10);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var interrupted = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (interrupted || !rig.Motion.IsMoving)
                return;
            interrupted = true;
            Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
            if (loseGrip)
                rig.Io.SetInput(InputIo.PcbSupplyIpmFixerForward, false);
            else
                stop.Cancel();
        };

        if (loseGrip)
            await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supplier.RunAsync(rig.Placement, stop.Token));
        else
            await rig.Supplier.RunAsync(rig.Placement, stop.Token);

        Assert.True(interrupted);
        Assert.False(rig.Motion.IsMoving);
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.False(rig.Supplier.IsHandoffRestartAllowed);
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task HandoffRequiresUnrotatedFeedback(bool rotated, bool unrotated)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInputs((InputIo.PcbSupplyRotated, rotated), (InputIo.PcbSupplyUnrotated, unrotated));
        var valid = !rotated && unrotated;
        Assert.Equal(valid ? PcbSupplyHandoff.Released : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        Assert.Equal(valid ? PcbSupplyHandoff.Holding : PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAxisFaultKeepsTheGripperClosed(bool duringRelease)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (rig.Supplier.Step is PcbSupplyState.HandingOff)
                rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        };
        if (duringRelease)
        {
            rig.Io.OutputChanged += (output, on) =>
            {
                if (output == OutputIo.PcbSupplyIpmFixerForward && !on
                    && rig.Supplier.Phase == PcbSupplyState.HandingOff)
                    rig.Motion.SetServo(MotionAxis.X, false);
            };
        }
        else
        {
            rig.Supplier.StepChanged += () =>
            {
                if (rig.Supplier.Step is PcbSupplyState.HandingOff)
                    rig.Motion.SetServo(MotionAxis.X, false);
            };
        }

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(rig.Placement, stop.Token));

        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.Equal(!duringRelease, rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
    }

    [Fact]
    public async Task NormalHandoffRejectsWrongRotationWithoutMovingOrReleasing()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (rig.Supplier.Step is PcbSupplyState.HandingOff)
                rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        };
        var lost = false;
        var commanded = false;
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.HandingOff)
            {
                lost = true;
                rig.Io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
            }
        };
        rig.Motion.MovingChanged += moving => commanded |= lost && moving;
        rig.Io.OutputChanged += (output, on) => commanded |= lost && output is OutputIo.PcbSupplyRotate
            or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(rig.Placement, timeout.Token));

        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rig.Supplier.PrepareHandoffAsync(timeout.Token));

        Assert.True(lost);
        Assert.False(commanded);
        Assert.True(rig.Supplier.PcbSecured);
        Assert.True(VirtualTestSupport.IsAt(rig.Supplier.Motion.Feedback, rig.Settings.HandoffPosition));
    }

    [Fact]
    public async Task RotationFeedbackLossDuringReleaseKeepsTheGripperClosed()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (rig.Supplier.Step is PcbSupplyState.HandingOff)
                rig.Placement.Handoff = PcbPlacementHandoff.Holding;
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on
                    && rig.Supplier.Phase == PcbSupplyState.HandingOff)
                rig.Io.SetInput(InputIo.PcbSupplyRotated, true);
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => rig.Supplier.RunAsync(rig.Placement, timeout.Token));

        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
        Assert.True(VirtualTestSupport.IsAt(rig.Supplier.Motion.Feedback, rig.Settings.HandoffPosition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffTravelStopsWhenRotationFeedbackChanges(bool leaving)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        var pickup = new PcbPickPosition { X = 20, Y = 10, Z = 3 };
        if (!leaving)
            await rig.Supplier.MoveFromHandoffAsync(pickup);
        var initialX = rig.Motion.Position.X;
        var lost = false;
        rig.Motion.PositionChanged += (x, y, z) =>
        {
            if (!lost && rig.Motion.IsMovingHorizontal && Math.Abs(x - initialX) > 0.1)
            {
                Assert.Equal(PcbSupplyHandoff.Unavailable, rig.Supplier.Handoff);
                lost = true;
                rig.Io.SetInputs((InputIo.PcbSupplyUnrotated, true), (InputIo.PcbSupplyRotated, false));
            }
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(() => leaving
            ? rig.Supplier.MoveFromHandoffAsync(pickup, timeout.Token)
            : rig.Supplier.PrepareHandoffAsync(timeout.Token));
        Assert.True(lost);
        Assert.False(rig.Motion.IsMoving);
        Assert.NotEqual(leaving ? pickup.X : rig.Settings.HandoffPosition.X, rig.Motion.Position.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffWaitsForRecipientHoldingAndDeparture(bool loseHoldingDuringRelease)
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        rig.Io.SetInput(InputIo.AutoMode, false);
        rig.Io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var atHandoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Supplier.StepChanged += () =>
        {
            if (rig.Supplier.Step is PcbSupplyState.PickingPcb)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            if (rig.Supplier.Step is PcbSupplyState.HandingOff)
                atHandoff.TrySetResult();
            if (rig.Supplier.Step is PcbSupplyState.WaitingForPlacementClear)
                released.TrySetResult();
            if (released.Task.IsCompleted && rig.Supplier.Step is PcbSupplyState.MovingToPickup)
                stop.Cancel();
        };
        rig.Io.OutputChanged += (output, on) =>
        {
            if (rig.Supplier.Phase != PcbSupplyState.HandingOff || on)
                return;
            if (output == OutputIo.PcbSupplyIpmFixerForward && loseHoldingDuringRelease)
                rig.Placement.Handoff = PcbPlacementHandoff.Unavailable;
            if (output == OutputIo.PcbSupplyGripperClosed)
                rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        };
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token);
        try
        {
            await atHandoff.Task.WaitAsync(stop.Token);
            Assert.True(rig.Supplier.PcbSecured);
            Assert.False(run.IsCompleted);
            rig.Placement.Handoff = PcbPlacementHandoff.Holding;
            if (loseHoldingDuringRelease)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
                Assert.Contains("before supply opened its gripper", error.Message);
                Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            }
            else
            {
                await released.Task.WaitAsync(stop.Token);
                Assert.True(rig.Supplier.PcbReleased);
                Assert.Equal(PcbSupplyState.WaitingForPlacementClear, rig.Supplier.Phase);
                Assert.True(VirtualTestSupport.IsAt(rig.Motion, rig.Settings.HandoffPosition));
                rig.Placement.Handoff = PcbPlacementHandoff.Clear;
                await run;
                Assert.Equal(PcbSupplyState.MovingToPickup, rig.Supplier.Phase);
            }
        }
        finally
        {
            stop.Cancel();
            if (!run.IsFaulted)
                await run;
        }
    }

    [Fact]
    public async Task StopAtSupplyHandoffPreservesRestartableGrip()
    {
        using var rig = new HandoffRig();
        await rig.InitializeAsync();
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)rig.Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        rig.Io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Supplier.Trace += message =>
        {
            if (message.StartsWith("Waiting for feedback"))
                waiting.TrySetResult();
        };
        var run = rig.Supplier.RunAsync(rig.Placement, stop.Token);
        await waiting.Task.WaitAsync(stop.Token);
        Assert.True(rig.Supplier.IsHandoffRestartAllowed);
        stop.Cancel();
        await run;
        Assert.True(rig.Supplier.IsHandoffRestartAllowed);
        Assert.True(rig.Supplier.PcbSecured);
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(rig.Io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.Equal(rig.Settings.HandoffPosition.Z, rig.Motion.Position.Z);
        Assert.False(rig.Motion.IsMoving);
    }

    private sealed class HandoffRig : IDisposable
    {
        public HandoffRig(bool probeFeedback = false)
        {
            Settings = new()
            {
                Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
                TravelZ = 3,
                HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
            };
            Io = new(new PcbSupplyHardwareSettings().Outputs, new MachineOptions { TimeoutMilliseconds = 500 });
            Motion = new(Settings.Motion, new());
            Units = new();

            IMotionFeedback feedback = Motion;
            if (probeFeedback)
            {
                feedback = System.Reflection.DispatchProxy.Create<IXyMotion, MachineTestSupport.ScopedMotionProbe>();
                FeedbackProbe = (MachineTestSupport.ScopedMotionProbe)feedback;
                FeedbackProbe.Motion = Motion;
                FeedbackProbe.ReportReady = true;
            }
            Recipes = new(OpenMachineStore(), new());
            Recipes.Current.PcbSupply = new()
            {
                Pcb1PickPosition = new() { X = 10, Y = 10, Z = 8 },
                Pcb2PickPosition = new() { X = 20, Y = 10, Z = 8 },
            };
            Supplier = new PcbSupplier((IXyMotion)feedback, new MotionStatus(feedback),
                Io,
                Settings,
                Recipes,
                Units);
            Placement = new();
        }

        public RecipeManager Recipes { get; }
        public PcbSupplySettings Settings { get; }
        public UnitSettings Units { get; }
        public VirtualIoService Io { get; }
        public VirtualMotionService Motion { get; }
        public PcbSupplier Supplier { get; }
        public MachineTestSupport.ScopedMotionProbe? FeedbackProbe { get; }
        public PlacementFeedback Placement { get; }

        public async Task InitializeAsync()
        {
            Io.Initialize();
            Motion.Initialize();
            await HomeAsync(Motion, 2_000);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false);
            await ((IIoService)Io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false);
            await Supplier.PrepareHandoffAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            Motion.Dispose();
        }
    }

    private sealed class PlacementFeedback : IPcbPlacementHandoff
    {
        public event Action? Changed;
        public PcbPlacementHandoff Handoff
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
