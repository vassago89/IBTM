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
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyRepeatKeepsPcbAboveSlotWithoutUpstreamAndChecksHolding(bool loseHolding)
    {
        var settings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 2_000, ZSpeed = 2_000 },
            RotationZ = 0,
            CarrierY = 10,
            HandoffPosition = new() { X = 80, Y = 30, Z = 2 },
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
        };
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions { TimeoutMilliseconds = 1_000 });
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.RotationZ);
        var simulation = new VirtualMachine(io, [motion]);
        motion.PositionChanged += (x, y, z) => simulation.UpdateSupplyPosition(
            x, y, z, settings.CarrierY,
            (recipe.Pcb1PickPosition.X, recipe.Pcb1PickPosition.Z),
            (recipe.Pcb2PickPosition.X, recipe.Pcb2PickPosition.Z), settings.HandoffPosition);
        var handler = new PcbSupplyHandler(motion, io, settings);
        var supplier = new PcbSupplier(handler, new() { PcbPlacement = false });
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
