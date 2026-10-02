using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Storage;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class PcbSupplyRepeatTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task PartialGripAwayFromSupportsBlocksDetectedPcbButAllowsEmptyPickup(bool repeat, bool pcbDetected)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            TravelZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());

        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.PcbSupply = recipe;
        var supplier = new PcbSupplier(motion, new MotionStatus(motion),
            io,
            settings,
            recipes,
            new() { PcbPlacement = false });
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
        await motion.MoveToXYAsync(30, 20, 2_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, pcbDetected);
        Assert.Equal(pcbDetected ? PcbSupplyPcbState.Detected : PcbSupplyPcbState.None, supplier.Pcb);
        var commanded = false;
        motion.MovingChanged += moving => commanded |= moving;
        io.OutputChanged += (output, on) => commanded |= output is OutputIo.PcbSupplyRotate
            or OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyIpmFixerForward;

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reachedPickup = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (x == 10 && y == 10 && z == 5)
            {
                reachedPickup = true;
                stop.Cancel();
            }
        };
        if (pcbDetected)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => supplier.RunAsync(new NoPlacement(), stop.Token, repeat));
            Assert.False(commanded);
            Assert.Equal(PcbSupplyCylinderState.Forward, supplier.Gripper);
        }
        else
        {
            await supplier.RunAsync(new NoPlacement(), stop.Token, repeat);
            Assert.True(reachedPickup);
            Assert.Equal(PcbSupplyCylinderState.Backward, supplier.Gripper);
            Assert.False(io.GetInput(InputIo.PcbSupplyIpmFixerForward));
        }

        Assert.False(motion.IsMoving);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PresenceDoesNotSkipPickupAndTravelRequiresHolding(bool repeat, bool loseGrip)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            TravelZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());

        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.PcbSupply = recipe;
        var supplier = new PcbSupplier(motion, new MotionStatus(motion),
            io,
            settings,
            recipes,
            new() { PcbPlacement = false });
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        supplier.StepChanged += () =>
        {
            if (supplier.Step is not PcbSupplyState.WaitingForCarrier)
                return;
            // Detection appears above pickup, after the empty-handler START check.
            io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            Assert.Equal(PcbSupplyPcbState.Detected, supplier.Pcb);
            Assert.False(supplier.PcbSecured);
        };

        var grippedAtPickup = false;
        io.InputChanged += (input, on) =>
        {
            if (input != InputIo.PcbSupplyGripperClosed || !on)
                return;
            var pickup = recipe.Pcb1PickPosition;
            grippedAtPickup = VirtualTestSupport.IsAt(supplier.Motion.Feedback, new() { X = pickup.X, Y = pickup.Y!.Value, Z = pickup.Z })
                && supplier.Rotation == PcbSupplyRotationState.Rotated;
        };

        var movedBeforeFixing = false;
        var reachedHandoff = false;
        var lostGrip = false;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        motion.MovingChanged += moving => movedBeforeFixing |= moving && grippedAtPickup && !supplier.PcbSecured;
        motion.PositionChanged += (x, y, z) =>
        {
            if (loseGrip && !lostGrip && supplier.PcbSecured && motion.IsMovingHorizontal && x > 30)
            {
                lostGrip = true;
                io.SetInput(InputIo.PcbSupplyIpmFixerForward, false);
            }
        };
        motion.StateChanged += () =>
        {
            if (!VirtualTestSupport.IsAt(supplier.Motion.Feedback, settings.HandoffPosition) || !supplier.PcbSecured)
                return;
            reachedHandoff = true;
            finish.Cancel();
        };
        if (loseGrip)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => supplier.RunAsync(new NoPlacement(), finish.Token, repeat));
            Assert.True(lostGrip);
            Assert.False(motion.IsMoving);
        }
        else
        {
            await supplier.RunAsync(new NoPlacement(), finish.Token, repeat);
        }
        Assert.Equal(!loseGrip, reachedHandoff);
        Assert.True(grippedAtPickup);
        Assert.False(movedBeforeFixing);
        Assert.Equal(!loseGrip, supplier.PcbSecured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyRepeatPlacesPcbAtPickupAndReusesNormalPickup(bool loseHolding)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            TravelZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions { TimeoutMilliseconds = 1_000 });
        using var motion = new VirtualMotionService(settings.Motion, new());
        var simulation = new VirtualMachine(io, [motion]);
        motion.PositionChanged += (x, y, z) => simulation.UpdateSupplyPosition(
            x, y, z,
            (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Y, recipe.Pcb1PickPosition.Z),
            (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Y, recipe.Pcb2PickPosition.Z), settings.HandoffPosition);

        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.PcbSupply = recipe;
        var supplier = new PcbSupplier(motion, new MotionStatus(motion),
            io,
            settings,
            recipes,
            new() { PcbPlacement = false });
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        io.SetInput(InputIo.AutoMode, false);
        var visits = 0;
        var releases = 0;
        var regrips = 0;
        var liftedEmpty = false;
        var lost = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PcbSupplyGripperClosed)
                return;
            Assert.Equal((10.0, 10.0, 5.0), motion.Position);
            Assert.Equal(PcbSupplyRotationState.Rotated, supplier.Rotation);
            if (!on)
            {
                Assert.False(io.GetInput(InputIo.PcbSupplyIpmFixerForward));
                Assert.True(supplier.UpstreamCarrierAvailable);
                releases++;
            }
            else if (releases > 0)
            {
                Assert.True(liftedEmpty);
                regrips++;
            }
        };
        supplier.StepChanged += () =>
        {
            if (supplier.Step is PcbSupplyState.HandingOff)
            {
                visits++;
                if (visits == 2)
                    stop.Cancel();
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (releases > 0 && supplier.PcbReleased && z == settings.TravelZ)
                liftedEmpty = true;
            if (loseHolding && !lost && supplier.Phase == PcbSupplyState.ReturningToPickup
                && motion.IsMovingHorizontal)
            {
                lost = true;
                io.SetInput(InputIo.PcbSupplyIpmFixerForward, false);
            }
        };
        if (loseHolding)
        {
            await Assert.ThrowsAsync<MotionInterlockException>(
                () => supplier.RunAsync(new NoPlacement(), stop.Token, repeat: true));
            Assert.True(lost);
            Assert.Equal(0, releases);
        }
        else
        {
            await supplier.RunAsync(new NoPlacement(), stop.Token, repeat: true);
            Assert.Equal(2, visits);
            Assert.Equal(1, releases);
            Assert.Equal(1, regrips);
            Assert.True(liftedEmpty);
            Assert.True(supplier.PcbSecured);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
            Assert.False(supplier.IsHandoffRestartAllowed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => supplier.RunAsync(new NoPlacement()));
            Assert.Equal(1, releases);
            Assert.Equal(PcbSupplyState.HandingOff, supplier.Phase);
        }
        Assert.False(motion.IsMoving);
    }

    private sealed class NoPlacement : IPcbPlacementHandoff
    {
        public event Action? Changed { add { } remove { } }
        public PcbPlacementHandoff Handoff => PcbPlacementHandoff.Unavailable;
        public HeatSinkSlot? ReturningPcb => null;
    }
}
