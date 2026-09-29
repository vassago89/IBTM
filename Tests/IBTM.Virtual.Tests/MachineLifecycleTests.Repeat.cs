using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IBTM.Virtual.Tests.MachineTest;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatSkipsEnabledFeedersAndReturnsBothPcbsTwice()
    {
        var settings = FlowSettings();
        settings.Units.PcbSupply = false;
        settings.Drivers.Bolt = BoltDriver.Virtual;
        settings.Units.PickupBoltFeeder = true;
        settings.Units.ShootingBoltFeeder = true;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        foreach (var heatSink in Enum.GetValues<HeatSinkSlot>())
            recipe.Pcb.BoltPoints.Add(new()
            {
                Id = VirtualTest.BoltId(2, heatSink),
                HeatSink = heatSink,
                Head = FasteningHead.Pickup,
                X = 15,
                Y = 10,
            });
        TeachInspectionFovs(recipe);
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 12 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 12 };
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var completed = new ConcurrentDictionary<long, HeatSinkAssembly[]>();
        var forbidden = new ConcurrentQueue<OutputIo>();
        var shootingStarts = 0;
        var pickupDescents = 0;
        var pickupStarts = 0;
        var pickupAttempts = 0;
        var handoffTrips = 0;
        var mainReturns = 0;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
        io.SetInput(InputIo.ShootingFeederBoltDetected, false);
        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        work.Changed += () =>
        {
            if (work.Completed)
                completed.TryAdd(work.CurrentJob.Id, work.Assemblies.ToArray());
        };
        services.GetRequiredService<PcbPlacer>().Trace += message =>
        {
            if (message.StartsWith($"PcbPlacer: {nameof(PcbPlacementState.MovingToHandoff)} ", StringComparison.Ordinal))
                Interlocked.Increment(ref handoffTrips);
        };
        io.OutputChanged += (output, on) =>
        {
            if (!on && output == OutputIo.MainConveyorRun
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                Interlocked.Increment(ref mainReturns);
            if (on && output is OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyReadyToFront1
                or OutputIo.ShootBolt or OutputIo.ShootingEscapeForward
                || !on && output == OutputIo.ShootingFeederOff)
                forbidden.Enqueue(output);
            if (on && output == OutputIo.PickupHeadVacuumPump)
            {
                Interlocked.Increment(ref pickupAttempts);
            }
            if (output == OutputIo.ShootingBoltStart)
            {
                if (on)
                {
                    Assert.True(io.GetInput(InputIo.ShootingHeadUp));
                    Assert.True(io.GetOutput(OutputIo.ShootingBoltPreset1));
                    Assert.False(io.GetOutput(OutputIo.ShootingBoltPreset3));
                    Assert.False(io.GetOutput(OutputIo.ShootingBoltPreset2));
                    Interlocked.Increment(ref shootingStarts);
                }
            }
            if (output == OutputIo.ShootingHeadDown && on)
            {
                Assert.True(io.GetOutput(OutputIo.ShootingBoltStart));
            }
            if (output == OutputIo.PickupBoltStart)
            {
                if (on)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    Interlocked.Increment(ref pickupStarts);
                }
            }
            if (output == OutputIo.PickupHeadDown && on)
            {
                Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
                Interlocked.Increment(ref pickupDescents);
            }
        };
        state.RepeatEnabled = true;
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => mainReturns >= 2 || state.IsError, TimeSpan.FromSeconds(55)),
                $"Returns={mainReturns}; Phase={machine.RepeatDisplayPhase}; Main={services.GetRequiredService<IBTM.Conveyor.MainConveyor>().Step}; {state.AlarmDetail}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(mainReturns >= 2);
            Assert.True(shootingStarts >= 4);
            Assert.True(pickupDescents >= 4);
            Assert.True(pickupStarts >= 4);
            Assert.Equal(0, pickupAttempts); // Repeat uses pickup travel without vacuum ON.
            Assert.True(handoffTrips >= 4);
            Assert.True(completed.Count >= 2);
            Assert.Empty(forbidden);
            foreach (var assemblies in completed.Values)
            {
                Assert.Equal(2, assemblies.Length);
                foreach (var assembly in assemblies)
                {
                    Assert.Equal(BoltResultSource.DryRun, Assert.Single(assembly.ShootingBoltResults).Value.Source);
                    Assert.Equal(BoltResultSource.DryRun, Assert.Single(assembly.PickupBoltResults).Value.Source);
                    Assert.Equal(AssemblyResult.Ok, assembly.FasteningResult);
                }
            }
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
        Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
    }

    [Fact]
    public async Task RepeatStartAllowsSupplyOnlyAndIntegratedNgConveyor()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            state.RepeatEnabled = true;
            Assert.True(state.RepeatEnabled);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);

            settings.Units.NgConveyor = true;
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProductionAllowsBothModesWhileRepeatRequiresManualAndSelectorChangeStops(
        bool repeat,
        bool manual)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Task run = Task.CompletedTask;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            state.RepeatEnabled = repeat;
            Assert.Equal(repeat, state.RepeatEnabled);
            VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

            // The physical selector is ON in TEACHING/MANUAL, OFF in AUTO.
            if (repeat)
            {
                io.SetInput(InputIo.AutoMode, false);
                Assert.Equal(StartBlockReason.TeachingMode, machine.StartBlock);
                Assert.False(machine.IsStartAllowed);
                await machine.StartAsync();
                Assert.False(state.AutomaticRunning);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            }

            io.SetInput(InputIo.AutoMode, manual);
            if (manual)
            {
                io.SetInput(InputIo.Door1Open, false);
                Assert.True(state.DoorInterlockReady);
            }
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            await WaitUntilAsync(() => machine.IsStartAllowed);
            run = machine.StartAsync();
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(3)),
                state.AlarmDetail);
            Assert.True(state.AutomaticRunning);

            if (manual)
            {
                io.SetInput(InputIo.Door1Open, true);
                io.SetInput(InputIo.Door1Open, false);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.True(state.AutomaticRunning);
                io.SetInput(InputIo.Door1Open, true);
            }
            io.SetInput(InputIo.AutoMode, !manual);
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(repeat ? StartBlockReason.TeachingMode : StartBlockReason.None, machine.StartBlock);
            await WaitUntilAsync(() => machine.IsStartAllowed == !repeat);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyMainRepeatWaitsForCarrierWithoutAnAlarm(bool enableNgTransfer)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = enableNgTransfer;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conveyor.Trace += message =>
        {
            if (message.StartsWith("MainConveyor: WaitingForFrontCarrier ", StringComparison.Ordinal))
                waiting.TrySetResult();
        };
        Task run = Task.CompletedTask;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            state.RepeatEnabled = true;
            await WaitUntilAsync(() => machine.IsStartAllowed);
            run = machine.StartAsync();
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(state.AutomaticRunning);
            Assert.False(services.GetRequiredService<IIoService>().GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatStartsForwardTransferWithOneOrTwoOccupiedStations(bool secondStationOccupied)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Task run = Task.CompletedTask;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            state.RepeatEnabled = true;
            io.SetInputs(
                (InputIo.PcbPlacementHeatSink2Present, true),
                (InputIo.BoltFasteningHeatSink2Present, secondStationOccupied));
            await services.GetRequiredService<PcbPlacer>().Station.SeatAsync(CancellationToken.None);
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            run = machine.StartAsync();
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(3)),
                state.AlarmDetail);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatMainReturnStopsWhenNgPickupDrops(bool inspectionEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        settings.Units.NgConveyor = inspectionEnabled;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        state.RepeatEnabled = true;
        // Seed real simulated heat sinks before exercising the return interlock.
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionHeatSink2Present, true));
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, true);
        var returned = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.MainConveyorRun && on
                && !io.GetOutput(OutputIo.MainConveyorForward))
            {
                returned = true;
                io.SetInput(InputIo.NgCarrierPickupUp, false);
                io.SetInput(InputIo.NgCarrierPickupDown, true);
            }
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await machine.StartAsync(timeout.Token);
            Assert.True(returned, $"Phase: {machine.RepeatDisplayPhase}; {state.AlarmDetail}");
            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatStopDiscardsReturnPhaseAndAllowsRestartWithoutReset()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        state.RepeatEnabled = true;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.AutoMode, true);
        var stopOutput = OutputIo.NgConveyorRun;
        void StopOnReverse(OutputIo output, bool on)
        {
            if (on && output == stopOutput
                && (output == OutputIo.NgConveyorRun
                    ? io.GetOutput(OutputIo.NgConveyorReverse)
                    : !io.GetOutput(OutputIo.MainConveyorForward)))
                machine.Stop();
        }

        io.OutputChanged += StopOnReverse;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => machine.RepeatDisplayPhase == RepeatPhase.Automatic);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.False(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(state.IsError);
        }
        finally
        {
            io.OutputChanged -= StopOnReverse;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatRunsAutoThroughDisabledStationsAndReturnsFromNgEndTwice()
    {
        var settings = FlowSettings();
        // Push duration is covered by the focused conveyor timing test.
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var visited = new ConcurrentDictionary<InputIo, int>();
        var forbidden = new ConcurrentQueue<OutputIo>();
        var ngReverse = 0;
        var mainReverse = 0;
        var mainReturns = 0;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        io.InputChanged += (input, on) =>
        {
            if (on)
                visited.AddOrUpdate(input, 1, (_, count) => count + 1);
        };
        io.OutputChanged += (output, on) =>
        {
            if (!on && output == OutputIo.MainConveyorRun
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                Interlocked.Increment(ref mainReturns);
            if (!on && output == OutputIo.ShootingFeederOff)
                forbidden.Enqueue(output);
            if (!on)
                return;
            if (output is OutputIo.MainConveyorReadyToFront2
                or OutputIo.MainConveyorAvailableToRear
                or OutputIo.ShootBolt)
                forbidden.Enqueue(output);
            if (output == OutputIo.NgConveyorRun && io.GetOutput(OutputIo.NgConveyorReverse))
            {
                Assert.True(io.GetInput(InputIo.NgShuttleDown));
                Interlocked.Increment(ref ngReverse);
            }
            if (output == OutputIo.MainConveyorRun && !io.GetOutput(OutputIo.MainConveyorForward))
            {
                Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
                Interlocked.Increment(ref mainReverse);
            }
        };

        state.RepeatEnabled = true;
        Assert.True(state.RepeatEnabled);
        io.SetInput(InputIo.AutoMode, true);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            Assert.True(
                await VirtualTest.WaitUntilAsync(
                    () => mainReturns >= 2 || state.IsError,
                    TimeSpan.FromSeconds(22)),
                $"Repeat timed out. Returns={mainReturns}, Phase={machine.RepeatDisplayPhase}, Main={services.GetRequiredService<IBTM.Conveyor.MainConveyor>().Step}, Alarm={state.AlarmMessage}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(mainReturns >= 2, state.AlarmDetail);
            Assert.True(ngReverse >= 2);
            Assert.True(mainReverse >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.NgConveyorPosition1Occupied) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.InspectionHeatSink1Present) >= 4);
            Assert.True(visited.GetValueOrDefault(InputIo.PcbPlacementHeatSink1Present) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.MainConveyorEntryCarrierDetected) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.PcbPlacementBackupPlateUp) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.BoltFasteningBackupPlateUp) >= 2);
            Assert.True(visited.GetValueOrDefault(InputIo.InspectionBackupPlateUp) >= 2);
            Assert.Empty(forbidden);
            Assert.Empty(services.GetRequiredService<PcbPlacer>().Station.Assemblies);
            Assert.Empty(services.GetRequiredService<BoltFasteningStation>().Station.Assemblies);
            Assert.Empty(services.GetRequiredService<InspectionStation>().Station.Assemblies);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }

        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
        Assert.False(state.AutomaticRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainOnlyRepeatRequiresPreparedStation3Support(bool preparedAtStart)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (preparedAtStart)
        {
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            await inspection.Station.SeatAsync(CancellationToken.None);
        }
        else
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        }
        var reachedStation3 = preparedAtStart;
        io.InputChanged += (input, on) =>
        {
            if (input == InputIo.InspectionHeatSink2Present && on)
                reachedStation3 = true;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var waitingForSupport = false;
        conveyor.StepChanged += () =>
        {
            if (!preparedAtStart && reachedStation3
                && conveyor.Step is MainConveyorState.WaitingForInspectionTransfer)
            {
                waitingForSupport = true;
                stop.Cancel();
            }
        };
        var returned = false;
        var raisedStation3 = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
                raisedStation3 = true;
            if (output == OutputIo.MainConveyorRun && !on
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
            {
                returned = true;
                stop.Cancel();
            }
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(reachedStation3);
            Assert.Equal(preparedAtStart, returned);
            Assert.Equal(!preparedAtStart, waitingForSupport);
            Assert.False(raisedStation3); // A disabled gantry cannot prepare the safe lifting position.
            Assert.Equal(preparedAtStart, io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MachineUnit.BoltFastening)]
    [InlineData(MachineUnit.Inspection)]
    public async Task StationRepeatWithoutMainStartsNextJobAfterCompletedWork(MachineUnit unit)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        ConveyorStation work = unit == MachineUnit.BoltFastening
            ? services.GetRequiredService<BoltFasteningStation>().Station : services.GetRequiredService<InspectionStation>().Station;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(unit == MachineUnit.BoltFastening
            ? InputIo.BoltFasteningHeatSink1Present : InputIo.InspectionHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var completed = 0;
        long previousJob = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        work.Changed += () =>
        {
            if (!work.Completed || work.CurrentJob.Id == previousJob)
                return;
            previousJob = work.CurrentJob.Id;
            completed++;
            Assert.NotEmpty(work.Assemblies);
            if (completed == 2)
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(2, completed);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InspectionOnlyRepeatPicksAndReturnsWithOrWithoutMaterial(
        bool startsWithCarrierHeld, bool detected, bool ngConveyorEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.NgConveyor = ngConveyorEnabled;
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 30, Y = 40 };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await inspection.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, startsWithCarrierHeld);
        // Material on the disabled main route does not belong to this repeat.
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        // The held-carrier turn does not use the shuttle as a support.
        io.SetInputs((InputIo.NgShuttleUp, false), (InputIo.NgShuttleDown, false));
        if (startsWithCarrierHeld)
        {
            await inspection.Station.SeatAsync(CancellationToken.None);
            await inspection.ExecuteTransferAsync(
                NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(inspection.Station.CarrierPresent);
        }
        else
        {
            // A closed empty gripper and an ON presence sensor are not a completed inspection.
            await inspection.SetGripperOpenAsync(false);
        }
        io.SetInput(InputIo.NgCarrierDetected, detected);

        (double X, double Y, bool Holding)[] expectedDescents = startsWithCarrierHeld
            ? [(5d, 20d, true), (5d, 20d, false), (5d, 20d, true)]
            : [(5d, 20d, false), (5d, 20d, true), (5d, 20d, false), (5d, 20d, true)];

        var descents = new ConcurrentQueue<(double X, double Y, bool Holding)>();
        var mainRan = false;
        var ngConveyorRan = false;
        var shuttleMoved = false;
        var plateRaised = false;
        var releasedAtShuttle = false;
        var raisedShuttleVisits = 0;
        var returnedTwice = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
            {
                var position = inspection.Motion.Feedback.Position;
                descents.Enqueue((position.X, position.Y,
                    inspection.Gripper == NgTransferGripperState.Closed));
            }
            if (output == OutputIo.NgCarrierGripperClose && !on
                && MotionService.IsAt(inspection.Motion.Feedback, settings.NgCarrierTransfer.ShuttlePlacePosition))
                releasedAtShuttle = true;
            mainRan |= output == OutputIo.MainConveyorRun && on;
            ngConveyorRan |= output == OutputIo.NgConveyorRun && on;
            shuttleMoved |= output == OutputIo.NgShuttleDown;
            plateRaised |= output == OutputIo.InspectionBackupPlateUp && on;
        };
        machine.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName != nameof(MachineController.RepeatDisplayPhase))
                return;
            if (machine.RepeatDisplayPhase == RepeatPhase.ReturnToStation3
                && MotionService.IsAt(inspection.Motion.Feedback, settings.NgCarrierTransfer.ShuttlePlacePosition)
                && inspection.IsRaised && inspection.Gripper == NgTransferGripperState.Closed)
                raisedShuttleVisits++;
            if (machine.RepeatDisplayPhase != RepeatPhase.Automatic
                || descents.Count != expectedDescents.Length || stop.IsCancellationRequested)
                return;
            returnedTwice = MotionService.IsAt(inspection.Motion.Feedback, settings.NgCarrierTransfer.WaitingPosition)
                && inspection.Station.CarrierPresent == startsWithCarrierHeld
                && inspection.Station.BackupPlate == StationCylinderState.Up && inspection.IsClear
                && inspection.Gripper == NgTransferGripperState.Open;
            stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(returnedTwice);
            Assert.Equal(2, raisedShuttleVisits);
            Assert.Equal(expectedDescents, descents.ToArray());
            if (!startsWithCarrierHeld)
                Assert.True(plateRaised);
            Assert.False(releasedAtShuttle);
            Assert.False(mainRan);
            Assert.False(ngConveyorRan);
            Assert.False(shuttleMoved);
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgTransferRepeatDoesNotCountPendingPickupAndStationPresenceAtStartup()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionStation>().ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 30 }, 10_000);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        var lowered = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                lowered = true;
        };
        machine.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MachineController.RepeatDisplayPhase)
                && machine.RepeatDisplayPhase == RepeatPhase.ReturnToStation3)
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(lowered);
            Assert.True(MotionService.IsAt(gantry.Motion.Feedback, settings.NgCarrierTransfer.ShuttlePlacePosition));
            Assert.True(gantry.IsRaised);
            Assert.True(gantry.IsTransferPending);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgRepeatWithoutMainReturnsFromPosition1ToShuttle()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        var reverse = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        io.OutputChanged += (output, on) =>
        {
            if (on && output == OutputIo.NgConveyorRun && io.GetOutput(OutputIo.NgConveyorReverse))
                reverse = true;
        };
        io.InputChanged += (input, on) =>
        {
            if (reverse && input == InputIo.NgShuttleUp && on && io.GetInput(InputIo.NgShuttleCarrierDetected))
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(reverse);
            Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
            Assert.True(io.GetInput(InputIo.NgShuttleUp));
            Assert.False(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    public enum PcbRepeatStopPoint
    {
        None,
        SupplyReturning,
        BothHolding,
        SupplyReleasing,
        PlacementHolding,
    }

    [Theory]
    [InlineData(PcbRepeatStopPoint.None)]
    [InlineData(PcbRepeatStopPoint.SupplyReturning)]
    [InlineData(PcbRepeatStopPoint.BothHolding)]
    [InlineData(PcbRepeatStopPoint.SupplyReleasing)]
    [InlineData(PcbRepeatStopPoint.PlacementHolding)]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatMainSupplyPlacementKeepsReturnedPcbsAndResumesForward(PcbRepeatStopPoint stopPoint)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.PcbSupply = true;
        settings.Units.PcbPlacement = true;
        // Keep an intermediate position observable when stopping reverse travel.
        settings.PcbSupply.Motion.HorizontalSpeed = 200;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        recipe.PcbSupply.Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 12 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 12 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var receivePosition = new AxisPosition
        {
            X = settings.PcbPlacementHandler.HandoffPosition.X,
            Y = settings.PcbPlacementHandler.HandoffPosition.Y,
            Z = settings.PcbPlacementHandler.ReceiveZ!.Value,
        };
        var handoffSteps = new ConcurrentQueue<string>();
        supply.Trace += handoffSteps.Enqueue;
        placement.Trace += handoffSteps.Enqueue;
        var returns = 0;
        var reverseHandoffs = 0;
        var mainReturned = false;
        var enteredDisabledStation = false;
        var unsafeRelease = false;
        var descendedToSourceSlot = false;
        var loweredPlacementIpm = false;
        var placementDepartedInY = false;
        var stopped = false;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.False(supply.UpstreamCarrierAvailable);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        io.InputChanged += (input, on) =>
        {
            if (on && input is InputIo.BoltFasteningHeatSink1Present or InputIo.InspectionHeatSink1Present)
                enteredDisabledStation = true;
        };
        io.OutputChanged += (output, on) =>
        {
            if (!on && output == OutputIo.MainConveyorRun
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                mainReturned = true;
            if (!on && output == OutputIo.PcbSupplyGripperClosed
                && supply.Rotation == PcbSupplyRotationState.Rotated)
                returns++;
            if (!on && output == OutputIo.PcbPlacementVacuumEjector && MotionService.IsAt(placement.Motion.Feedback, receivePosition))
            {
                reverseHandoffs++;
                unsafeRelease |= !supply.PcbSecured;
            }
            loweredPlacementIpm |= output == OutputIo.PcbPlacementIpmDown && on;
        };
        void StopAtHandoff()
        {
            if (stopped || stopPoint == PcbRepeatStopPoint.None)
                return;
            var reached = stopPoint switch
            {
                PcbRepeatStopPoint.SupplyReturning => reverseHandoffs == 2 && supply.PcbSecured
                    && supply.Phase == PcbSupplyState.MovingToPickup && supply.Motion.Feedback.IsMovingHorizontal
                    && supply.Motion.Feedback.Position.X < settings.PcbSupply.HandoffPosition.X - 1
                    && supply.Motion.Feedback.Position.X > recipe.PcbSupply.Pcb2PickPosition.X + 1,
                PcbRepeatStopPoint.BothHolding => supply.PcbSecured && placement.PcbSecured
                    && MotionService.IsAt(placement.Motion.Feedback, receivePosition),
                PcbRepeatStopPoint.SupplyReleasing => reverseHandoffs > 0
                    && !io.GetInput(InputIo.PcbSupplyIpmFixerForward) && supply.Gripper == PcbSupplyCylinderState.Forward
                    && placement.PcbSecured && MotionService.IsAt(placement.Motion.Feedback, receivePosition),
                PcbRepeatStopPoint.PlacementHolding => reverseHandoffs > 0 && supply.PcbReleased
                    && placement.PcbSecured && MotionService.IsAt(placement.Motion.Feedback, receivePosition),
                _ => false,
            };
            if (reached)
            {
                stopped = true;
                machine.Stop();
            }
        }
        supply.Changed += StopAtHandoff;
        placement.Changed += StopAtHandoff;
        supply.Motion.Feedback.PositionChanged += (x, y, z) => StopAtHandoff();
        placement.Motion.Feedback.PositionChanged += (x, y, z) =>
        {
            if (placement.Phase is PcbPlacementState.WaitingForSupply or PcbPlacementState.WaitingForSupplyRelease
                && y > settings.PcbPlacementHandler.HandoffPosition.Y
                && y < recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y)
            {
                placementDepartedInY = true;
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.X, x);
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, z);
                Assert.NotEqual(PcbPlacementHandoff.Clear, placement.Handoff);
            }
        };
        supply.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: MovingToPickup ", StringComparison.Ordinal))
                Assert.Equal(recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y, placement.Motion.Feedback.Position.Y);
        };
        supply.Motion.Feedback.StateChanged += () =>
        {
            if (supply.PcbSecured && supply.Rotation == PcbSupplyRotationState.Rotated
                && !MotionService.IsAtZ(supply.Motion.Feedback, settings.PcbSupply.RotationZ))
                descendedToSourceSlot = true;
            StopAtHandoff();
        };
        state.RepeatEnabled = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            if (stopPoint != PcbRepeatStopPoint.None)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(stopped,
                    $"Supply={supply.Phase}, Placement={placement.Phase}, {state.AlarmDetail}");
                Assert.False(state.IsError, state.AlarmDetail);
                Assert.True(supply.PcbSecured || placement.PcbSecured);
                run = machine.StartAsync(timeout.Token);
            }
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => mainReturned || state.IsError, TimeSpan.FromSeconds(27)),
                $"Supply={supply.Phase}/{supply.Handoff}/{supply.Pcb}/{supply.Rotation}/{supply.Motion.Feedback.Position}, "
                    + $"Placement={placement.Phase}/{placement.Handoff}/{placement.Pcb}/{placement.IpmLift}/{placement.Motion.Feedback.Position}, "
                    + $"Phase={machine.RepeatDisplayPhase}, {state.AlarmDetail}\n"
                    + string.Join('\n', handoffSteps));
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(0, returns);
            Assert.False(descendedToSourceSlot);
            Assert.False(loweredPlacementIpm);
            Assert.Equal(stopPoint == PcbRepeatStopPoint.BothHolding ? 1 : 2, reverseHandoffs);
            Assert.False(unsafeRelease);
            Assert.True(placementDepartedInY);
            Assert.False(enteredDisabledStation);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(mainReturned);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
