using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

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
            RotationZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.RotationZ);

        var supplier = new PcbSupplier(motion,
            io,
            settings,
            new() { PcbPlacement = false });
        var handler = supplier;
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await handler.SetRotatedAsync(true);
        await motion.MoveToXYAsync(30, 20, 2_000);
        await handler.SetGripperClosedAsync(true);
        await handler.SetIpmFixerAsync(false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, pcbDetected);
        Assert.Equal(pcbDetected ? PcbSupplyPcbState.Detected : PcbSupplyPcbState.None, handler.Pcb);
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
                () => supplier.RunAsync(recipe, new NoPlacement(), stop.Token, repeat));
            Assert.False(commanded);
            Assert.Equal(PcbSupplyCylinderState.Forward, handler.Gripper);
        }
        else
        {
            await supplier.RunAsync(recipe, new NoPlacement(), stop.Token, repeat);
            Assert.True(reachedPickup);
            Assert.Equal(PcbSupplyCylinderState.Backward, handler.Gripper);
            Assert.False(handler.IpmFixed);
        }

        Assert.False(motion.IsMoving);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PresenceDoesNotSkipPickupAndInterruptedGripResumesAtTheSlot(bool repeat, bool loseGrip)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            RotationZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.RotationZ);

        var supplier = new PcbSupplier(motion,
            io,
            settings,
            new() { PcbPlacement = false });
        var handler = supplier;
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await handler.SetGripperClosedAsync(false);
        await handler.SetIpmFixerAsync(false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        Assert.Equal(PcbSupplyPcbState.Detected, handler.Pcb);
        Assert.False(supplier.PcbSecured);
        Assert.NotEqual(PcbSupplyState.MovingToHandoff, supplier.State);

        var grippedAtPickup = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        void StopAfterGrip(InputIo input, bool on)
        {
            if (input != InputIo.PcbSupplyGripperClosed || !on)
                return;
            grippedAtPickup = handler.IsAtPickup(recipe.Pcb1PickPosition)
                && handler.Rotation == PcbSupplyRotationState.Rotated;
            stop.Cancel();
        }
        io.InputChanged += StopAfterGrip;
        await supplier.RunAsync(recipe, new NoPlacement(), stop.Token, repeat);
        io.InputChanged -= StopAfterGrip;
        Assert.True(grippedAtPickup);
        Assert.False(handler.IpmFixed);

        var movedBeforeFixing = false;
        var reachedHandoff = false;
        var lostGrip = false;
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        motion.MovingChanged += moving => movedBeforeFixing |= moving && !handler.PcbSecured;
        motion.PositionChanged += (x, y, z) =>
        {
            if (loseGrip && !lostGrip && handler.PcbSecured && motion.IsMovingHorizontal && x > 30)
            {
                lostGrip = true;
                io.SetInput(InputIo.PcbSupplyIpmFixerForward, false);
            }
        };
        motion.StateChanged += () =>
        {
            if (!handler.IsAtHandoff() || !handler.PcbSecured)
                return;
            reachedHandoff = true;
            finish.Cancel();
        };
        if (loseGrip)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => supplier.RunAsync(recipe, new NoPlacement(), finish.Token, repeat));
            Assert.True(lostGrip);
            Assert.False(motion.IsMoving);
        }
        else
        {
            await supplier.RunAsync(recipe, new NoPlacement(), finish.Token, repeat);
        }
        Assert.Equal(!loseGrip, reachedHandoff);
        Assert.False(movedBeforeFixing);
        Assert.Equal(!loseGrip, handler.PcbSecured);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyRepeatKeepsPcbAboveSlotWithoutUpstreamAndChecksHolding(bool loseHolding)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            RotationZ = 0,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions { TimeoutMilliseconds = 1_000 });
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.RotationZ);
        var simulation = new VirtualMachine(io, [motion]);
        motion.PositionChanged += (x, y, z) => simulation.UpdateSupplyPosition(
            x, y, z,
            (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Y, recipe.Pcb1PickPosition.Z),
            (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Y, recipe.Pcb2PickPosition.Z), settings.HandoffPosition);

        var supplier = new PcbSupplier(motion,
            io,
            settings,
            new() { PcbPlacement = false });
        var handler = supplier;
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        io.SetInput(InputIo.AutoMode, false);
        var visits = 0;
        var returns = 0;
        var releases = 0;
        var descendedAfterPickup = false;
        var returning = false;
        var lost = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyGripperClosed && !on)
                releases++;
        };
        motion.StateChanged += () =>
        {
            if (!returning && handler.IsAtHandoff() && handler.PcbSecured)
            {
                visits++;
                returning = true;
                io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            }
            if (returning && handler.Rotation == PcbSupplyRotationState.Rotated
                && handler.IsAtPickupXY(recipe.Pcb1PickPosition) && handler.IsAtRotationZ())
            {
                returning = false;
                returns++;
                if (returns == 2)
                    stop.Cancel();
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (visits > 0 && z == recipe.Pcb1PickPosition.Z)
                descendedAfterPickup = true;
            if (loseHolding && !lost && handler.PcbSecured && motion.IsMovingHorizontal && x > 30)
            {
                lost = true;
                io.SetInput(InputIo.PcbSupplyIpmFixerForward, false);
            }
        };
        if (loseHolding)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => supplier.RunAsync(recipe, new NoPlacement(), stop.Token, repeat: true));
            Assert.True(lost);
            Assert.Equal(0, returns);
        }
        else
        {
            await supplier.RunAsync(recipe, new NoPlacement(), stop.Token, repeat: true);
            Assert.Equal(2, returns);
            Assert.True(visits >= 2);
        }
        Assert.Equal(0, releases);
        Assert.False(descendedAfterPickup);
        if (!loseHolding)
            Assert.True(handler.PcbSecured);
        Assert.False(motion.IsMoving);
    }

    private sealed class NoPlacement : IPcbPlacementHandoff
    {
        public event Action? Changed { add { } remove { } }
        public PcbPlacementHandoff Handoff => PcbPlacementHandoff.Unavailable;
        public HeatSinkSlot? ReturningPcb => null;
    }
}
