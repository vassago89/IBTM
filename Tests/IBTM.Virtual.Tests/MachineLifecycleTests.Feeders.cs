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
    [InlineData(InputIo.PickupFeederBoltDetected, MachineAlarm.PickupBoltFeeder)]
    [InlineData(InputIo.ShootingFeederBoltDetected, MachineAlarm.ShootingBoltFeeder)]
    public async Task SharedFeederReportsEmptySideDespiteOtherFeederChanges(InputIo emptyInput, MachineAlarm alarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PickupBoltFeeder);
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 200;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingEscapeForward, false);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        var otherInput = emptyInput == InputIo.PickupFeederBoltDetected
            ? InputIo.ShootingFeederBoltDetected : InputIo.PickupFeederBoltDetected;
        io.SetInput(emptyInput, false);
        io.SetInput(otherInput, true);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            // Repeated edges from one feeder cannot extend the other feeder's empty deadline.
            for (var change = 0; change < 20 && !state.IsError; change++)
            {
                io.SetInput(otherInput, false);
                io.SetInput(otherInput, true);
                await Task.Delay(30);
            }
            Assert.Equal(alarm, state.Alarm);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.Contains(emptyInput.GetDescription(), state.AlarmDetail);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task FeedersOffOrRepeatKeepPickupMotionAndStartBothIoHeads(
        bool pickupEnabled, bool shootingEnabled, bool repeat)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = pickupEnabled;
        settings.Units.ShootingBoltFeeder = shootingEnabled;
        var pickupFeeding = pickupEnabled && !repeat;
        var shootingFeeding = shootingEnabled && !repeat;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Number = 1, Head = FasteningHead.Shooting, X = 10, Y = 10,
                FasteningX = 10.5, FasteningY = 9.5, FasteningZOffset = 0.25 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = 20, Y = 10,
                FasteningX = 21, FasteningY = 11, FasteningZOffset = -0.5 },
            new() { Number = 3, Head = FasteningHead.Pickup, X = 30, Y = 10 },
        ];
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var outputs = new ConcurrentQueue<(OutputIo Output, bool On)>();
        var starts = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var descents = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var pickups = new ConcurrentQueue<(double X, double Y, double Z)>();
        var visitedPickupFeeder = false;
        var feederRan = false;
        services.GetRequiredService<BoltFeederUnit>().Trace += message => feederRan = true;
        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, pickupFeeding);
        io.SetInput(InputIo.ShootingFeederBoltDetected, shootingFeeding);
        if (!shootingFeeding)
            io.SetInput(InputIo.ShootingTubeBoltDetected, true); // Disabled supply does not wait for the tube.
        io.SetInput(InputIo.AutoMode, repeat);
        state.RepeatEnabled = repeat;
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
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
            if (on && output is OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown)
            {
                var position = gantry.Feedback.GetPosition();
                Assert.True(io.GetOutput(output == OutputIo.ShootingHeadDown
                    ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart));
                descents.Enqueue((output == OutputIo.ShootingHeadDown ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
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
            Assert.True(work.Completed, services.GetRequiredService<BoltFasteningStation>().GetNextStep().ToString());
            var assembly = Assert.Single(work.Assemblies);
            Assert.Equal(shootingFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun,
                Assert.Single(assembly.PcbBoltResults).Value.Source);
            Assert.Equal(2, assembly.PickupBoltResults.Count);
            Assert.All(assembly.PickupBoltResults.Values, result =>
                Assert.Equal(pickupFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun, result.Source));
            Assert.All(assembly.PcbBoltResults.Values.Concat(assembly.PickupBoltResults.Values), result =>
                Assert.Equal(result.Source == BoltResultSource.Controller, result.Torque is not null));
            Assert.Equal(AssemblyResult.Ok, assembly.FasteningResult);
            var positions = new[]
            {
                (FasteningHead.Shooting, 10.5, 9.5, 8.25),
                (FasteningHead.Pickup, 21d, 11d, 11.5),
                (FasteningHead.Pickup, 30d, 10d, 12d),
            };
            Assert.Equal(positions, descents.ToArray());
            Assert.Equal(positions, starts.ToArray());
            var operation = services.GetRequiredService<OperationViewModel>();
            Assert.All(operation.BoltTargets, bolt => Assert.Equal(BoltTargetState.Ok, bolt.State));
            Assert.True(visitedPickupFeeder);
            if (pickupFeeding)
                Assert.Equal(new[] { (100d, 50d, 10d), (100d, 50d, 10d) }, pickups.ToArray());
            else
                Assert.Empty(pickups);
            Assert.Equal(pickupEnabled, settings.Units.PickupBoltFeeder);
            Assert.Equal(shootingEnabled, settings.Units.ShootingBoltFeeder);
            Assert.Equal(pickupFeeding || shootingFeeding, feederRan);
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
            recipe.Pcb.BoltPoints[0].FasteningX = null;
            Assert.False(machine.TeachingReady); // Feeder OFF still requires taught fastening coordinates.
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.Completed)]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.Cancelled)]
    [InlineData(FasteningHead.Shooting, BoltDriver.Virtual, DryRunEnd.MissingUpFeedback)]
    [InlineData(FasteningHead.Pickup, BoltDriver.Virtual, DryRunEnd.Completed)]
    public async Task FasteningWithoutDownFeedbackStillRequiresUpFeedbackAndStopsOnCancellation(
        FasteningHead head, BoltDriver driver, DryRunEnd end)
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = driver;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        var stopDuringDescent = end == DryRunEnd.Cancelled;
        var missingUpFeedback = end == DryRunEnd.MissingUpFeedback;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = head, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        if (head == FasteningHead.Pickup)
            await station.SetPickupTableDownAsync(true, CancellationToken.None);
        settings.Options.TimeoutMilliseconds = 100;
        io.AutoResponseEnabled = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var descended = false;
        var raising = false;
        var (start, cylinder, up, down) = head == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        io.OutputChanged += (output, on) =>
        {
            if (output != cylinder)
                return;
            if (on)
            {
                Assert.True(io.GetOutput(start));
                descended = true;
                io.SetInputs((up, false), (down, false));
                if (stopDuringDescent)
                    stop.Cancel();
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
            var run = station.RunAsync(stop.Token);
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
                Assert.Equal(BoltResultSource.DryRun,
                    Assert.Single(results).Value.Source);
            Assert.False(io.GetOutput(start));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    public enum DryRunEnd
    {
        Completed,
        Cancelled,
        MissingUpFeedback,
    }

    [Fact]
    public async Task DisabledPickupFeederKeepsLiftInterlockBeforeNewCarrier()
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Number = 1, Head = FasteningHead.Pickup, X = 20, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var pickups = 0;
        var starts = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                Assert.True(station.IsHorizontalMoveAllowed);
                starts++;
            }
            if (output == OutputIo.PickupHeadVacuumPump && on)
            {
                Assert.True(station.IsAtPickupPosition());
                Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
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
            Assert.True(station.IsAtPickupXY());
            Assert.True(station.IsAtSafeZ());
            Assert.Equal(BoltCylinderState.Down, station.PickupHeadPosition);
            Assert.False(station.PickupBoltLoaded);
            Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);

            settings.Options.TimeoutMilliseconds = 2_000;
            io.AutoResponseEnabled = true;
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            await station.SetVacuumAsync(FasteningHead.Pickup, false, CancellationToken.None);
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
            Assert.False(station.PickupBoltLoaded);
            Assert.Equal(1, starts); // Only the new carrier reaches fastening START.
            Assert.Equal(AssemblyResult.Ok, work.GetAssembly(HeatSinkSlot.HeatSink1).FasteningResult);
            Assert.True(station.IsHorizontalMoveAllowed);
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }
}
