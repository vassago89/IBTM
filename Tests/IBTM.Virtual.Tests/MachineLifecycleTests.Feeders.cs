using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Core;
using IBTM.Device;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task FeedersOffOrRepeatKeepPickupMotionAndStartBothIoHeads(
        bool pickupEnabled, bool shootingEnabled, bool repeat)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = pickupEnabled;
        settings.Units.ShootingBoltFeeder = shootingEnabled;
        var pickupFeeding = pickupEnabled && !repeat;
        var shootingFeeding = shootingEnabled && !repeat;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = 20, Y = 10 },
            new() { Number = 3, Head = FasteningHead.Pickup, X = 30, Y = 10 },
        ];
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var outputs = new ConcurrentQueue<(OutputIo Output, bool On)>();
        var starts = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var descents = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var pickups = new ConcurrentQueue<(double X, double Y, double Z)>();
        var visitedPickupFeeder = false;
        var pickupFeederRan = false;
        var shootingFeederRan = false;
        services.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Pickup).Trace += message => pickupFeederRan = true;
        services.GetRequiredKeyedService<BoltFeederUnit>(FasteningHead.Shooting).Trace += message => shootingFeederRan = true;
        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, pickupFeeding);
        io.SetInput(InputIo.ShootingFeederBoltDetected, shootingFeeding);
        if (!shootingFeeding)
            io.SetInput(InputIo.ShootingTubeBoltDetected, true); // Disabled supply does not wait for the tube.
        io.SetInput(InputIo.AutoMode, repeat);
        state.RepeatEnabled = repeat;
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        // Standalone fastening starts from plate UP even if the stopper is still UP.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.BoltFasteningStopperUp, true);
        gantry.Feedback.PositionChanged += (x, y, _) =>
        {
            if (Math.Abs(x - settings.BoltFastening.PickupPosition.X) < 0.01
                && Math.Abs(y - settings.BoltFastening.PickupPosition.Y) < 0.01)
                visitedPickupFeeder = true;
        };
        io.OutputChanged += (output, on) =>
        {
            outputs.Enqueue((output, on));
            if (on && output == OutputIo.PickupHeadVacuumPump)
            {
                Assert.True(gantry.IsAtPickupPosition());
                Assert.Equal(BoltCylinderState.Up, gantry.PickupHeadPosition);
                Assert.Equal(BoltCylinderState.Up, gantry.ShootingHeadPosition);
                Assert.Equal(BoltCylinderState.Down, gantry.PickupTablePosition);
                var position = gantry.Feedback.GetPosition();
                pickups.Enqueue((position.X, position.Y, position.Z));
            }
            if (on && output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed); // START precedes cylinder descent.
                var shooting = output == OutputIo.ShootingBoltStart;
                Assert.True(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset1 : OutputIo.PickupBoltPreset1));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset2 : OutputIo.PickupBoltPreset2));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset3 : OutputIo.PickupBoltPreset3));
                var position = gantry.Feedback.GetPosition();
                starts.Enqueue((output == OutputIo.ShootingBoltStart ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
            if (output == OutputIo.ShootingBoltStart)
                io.SetInput(InputIo.ShootingBoltFasten, on);
            if (output == OutputIo.PickupBoltStart)
                io.SetInput(InputIo.PickupBoltFasten, on);
            if (on && output is OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown)
            {
                var position = gantry.Feedback.GetPosition();
                Assert.True(io.GetOutput(output == OutputIo.ShootingHeadDown
                    ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart));
                descents.Enqueue((output == OutputIo.ShootingHeadDown ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
            if (output == OutputIo.ShootingHeadDown && on)
            {
                Assert.True(io.GetOutput(OutputIo.ShootingBoltStart));
                io.SetInput(InputIo.ShootingBoltFasten, false);
            }
            if (output == OutputIo.PickupHeadDown && on && io.GetOutput(OutputIo.PickupBoltStart))
                io.SetInput(InputIo.PickupBoltFasten, false);
        };
        work.Changed += () =>
        {
            if (work.Completed)
                machine.Stop();
        };
        try
        {
            Assert.Equal(repeat, state.RepeatEnabled);
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(work.Completed, services.GetRequiredService<BoltFasteningStation>().GetState().ToString());
            var assembly = Assert.Single(work.Assemblies);
            Assert.Equal(BoltResultSource.IoAssumedOk,
                Assert.Single(assembly.PcbBoltResults).Value.Source);
            Assert.Equal(2, assembly.PickupBoltResults.Count);
            Assert.All(assembly.PickupBoltResults.Values, result =>
                Assert.Equal(BoltResultSource.IoAssumedOk, result.Source));
            Assert.All(assembly.PcbBoltResults.Values.Concat(assembly.PickupBoltResults.Values), result => Assert.Null(result.Torque));
            Assert.Equal(AssemblyResult.Ok, assembly.FasteningResult);
            var positions = new[]
            {
                (FasteningHead.Shooting, 10d, 10d, 8d),
                (FasteningHead.Pickup, 20d, 10d, 12d),
                (FasteningHead.Pickup, 30d, 10d, 12d),
            };
            Assert.Equal(positions, descents.ToArray());
            Assert.Equal(positions, starts.ToArray());
            var operation = services.GetRequiredService<OperationViewModel>();
            Assert.All(operation.BoltTargets, bolt => Assert.Equal(BoltTargetState.Ok, bolt.State));
            Assert.True(visitedPickupFeeder);
            Assert.Equal(new[] { (100d, 50d, 10d), (100d, 50d, 10d) }, pickups.ToArray());
            Assert.Equal(pickupEnabled, settings.Units.PickupBoltFeeder);
            Assert.Equal(shootingEnabled, settings.Units.ShootingBoltFeeder);
            Assert.Equal(pickupFeeding, pickupFeederRan);
            Assert.Equal(shootingFeeding, shootingFeederRan);
            if (!pickupFeeding)
            {
                Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
                Assert.False(gantry.PickupBoltLoaded);
            }
            if (!shootingFeeding)
                Assert.DoesNotContain(outputs, command =>
                    command.On && command.Output is OutputIo.ShootingHeadVacuumPump
                        or OutputIo.ShootBolt or OutputIo.ShootingEscapeForward
                    || !command.On && command.Output == OutputIo.ShootingFeederOff);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.True(io.GetInput(InputIo.PickupHeadUp));
            Assert.True(io.GetInput(InputIo.ShootingHeadUp));
            recipe.Pcb.BoltPoints[0].X = null;
            Assert.False(machine.TeachingReady); // Feeder OFF still requires taught fastening coordinates.
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false, FasteningHead.Pickup, true)]
    [InlineData(false, false, false, FasteningHead.Shooting, true)]
    public async Task FasteningWithoutDownFeedbackStillRequiresUpFeedbackAndStopsOnCancellation(
        bool stopDuringDescent,
        bool missingUpFeedback,
        bool repeat,
        FasteningHead head = FasteningHead.Shooting,
        bool useAdc = false)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = useAdc ? BoltDriver.Virtual : BoltDriver.Io;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = repeat;
        settings.Units.ShootingBoltFeeder = repeat;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = head, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        if (head == FasteningHead.Pickup)
            await station.SetPickupTableDownAsync(true, CancellationToken.None);
        settings.Options.TimeoutMilliseconds = 100;
        io.AutoResponseEnabled = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var descended = false;
        var raising = false;
        var (start, fasten, cylinder, up, down) = head == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, InputIo.PickupBoltFasten, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, InputIo.ShootingBoltFasten, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        io.OutputChanged += (output, on) =>
        {
            if (output == start)
                io.SetInput(fasten, on);
            if (output != cylinder)
                return;
            if (on)
            {
                if (!useAdc)
                    Assert.True(io.GetOutput(start));
                descended = true;
                io.SetInputs((up, false), (down, false));
                if (stopDuringDescent)
                    stop.Cancel();
                else if (!useAdc)
                    io.SetInput(fasten, false);
            }
            else if (descended)
            {
                raising = true;
                io.SetInput(up, !missingUpFeedback);
            }
        };
        work.Changed += () =>
        {
            if (work.Completed)
                stop.Cancel();
        };
        try
        {
            var run = station.RunAsync(stop.Token, repeat: repeat);
            if (missingUpFeedback)
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(descended);
            Assert.False(io.GetInput(down));
            Assert.Equal(!stopDuringDescent, raising);
            Assert.Equal(!stopDuringDescent && !missingUpFeedback, work.Completed);
            var assembly = Assert.Single(work.Assemblies);
            var results = head == FasteningHead.Pickup ? assembly.PickupBoltResults : assembly.PcbBoltResults;
            if (stopDuringDescent)
                Assert.Empty(results);
            else
                Assert.Equal(useAdc ? BoltResultSource.Controller : BoltResultSource.IoAssumedOk,
                    Assert.Single(results).Value.Source);
            Assert.Equal(stopDuringDescent, station.HasPendingResult);
            Assert.False(io.GetOutput(start));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisabledPickupFeederKeepsLiftInterlockBeforeNewCarrier()
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = FasteningHead.Pickup, X = 20, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var pickups = 0;
        var starts = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart)
            {
                if (on)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    starts++;
                }
                io.SetInput(InputIo.PickupBoltFasten, on);
            }
            if (output == OutputIo.PickupHeadDown && on && io.GetOutput(OutputIo.PickupBoltStart))
                io.SetInput(InputIo.PickupBoltFasten, false);
            if (output == OutputIo.PickupHeadVacuumPump && on)
            {
                Assert.True(gantry.IsAtPickupPosition());
                Assert.Equal(BoltCylinderState.Up, gantry.PickupHeadPosition);
                pickups++;
                if (pickups == 1)
                {
                    settings.Options.TimeoutMilliseconds = 100;
                    io.AutoResponseEnabled = false;
                    // External loss of UP feedback must still block travel after pickup.
                    io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
                }
            }
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IoTimeoutException>(() => station.RunAsync(timeout.Token));
            Assert.Equal(1, pickups);
            Assert.Equal(0, starts);
            Assert.True(gantry.IsAtPickupXY());
            Assert.True(gantry.IsAtSafeZ());
            Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
            Assert.False(gantry.PickupBoltLoaded);
            Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);
            Assert.False(station.HasPendingResult);

            settings.Options.TimeoutMilliseconds = 2_000;
            io.AutoResponseEnabled = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            station.DiscardRemovedCarrierResults();
            await gantry.SetVacuumAsync(FasteningHead.Pickup, false, CancellationToken.None);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            work.Changed += () =>
            {
                if (work.Completed)
                    stop.Cancel();
            };
            await station.RunAsync(stop.Token);
            Assert.True(work.Completed);
            Assert.Equal(2, pickups);
            Assert.False(gantry.PickupBoltLoaded);
            Assert.Equal(1, starts); // Only the new carrier reaches fastening START.
            Assert.Equal(AssemblyResult.Ok, work.GetAssembly(HeatSinkSlot.HeatSink1).FasteningResult);
            Assert.True(gantry.IsHorizontalMoveAllowed);
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }
}
