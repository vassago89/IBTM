using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.BoltFastening;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IBTM.Storage;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatWithPickupFeederOffStartsBothIoHeadsAndReturnsBothPcbsTwice()
    {
        var settings = FlowSettings();
        settings.Drivers.Bolt = BoltDriver.Io;
        settings.Units.PickupBoltFeeder = false;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        foreach (var heatSink in Enum.GetValues<HeatSinkSlot>())
            recipe.Pcb.BoltPoints.Add(new()
            {
                Number = 2,
                HeatSink = heatSink,
                Head = FasteningHead.Pickup,
                X = 15,
                Y = 10,
            });
        TeachInspectionFovs(settings, recipe);
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 12 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 12 };
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var completed = new ConcurrentDictionary<long, HeatSinkAssembly[]>();
        var forbidden = new ConcurrentQueue<OutputIo>();
        var shootingStarts = 0;
        var pickupDescents = 0;
        var pickupStarts = 0;
        var pickupAttempts = 0;
        var handoffTrips = 0;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
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
            if (on && output is OutputIo.PcbSupplyGripperClosed or OutputIo.PcbSupplyReadyToFront1)
                forbidden.Enqueue(output);
            if (on && output == OutputIo.PickupHeadVacuumPump)
            {
                Assert.True(gantry.IsAtPickupPosition());
                Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
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
                io.SetInput(InputIo.ShootingBoltFasten, on);
            }
            if (output == OutputIo.ShootingHeadDown && on)
            {
                Assert.True(io.GetOutput(OutputIo.ShootingBoltStart));
                io.SetInput(InputIo.ShootingBoltFasten, false);
            }
            if (output == OutputIo.PickupBoltStart)
            {
                if (on)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    Interlocked.Increment(ref pickupStarts);
                }
                io.SetInput(InputIo.PickupBoltFasten, on);
            }
            if (output == OutputIo.PickupHeadDown && on)
            {
                if (!gantry.IsAtPickupXY())
                {
                    Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
                    Interlocked.Increment(ref pickupDescents);
                    io.SetInput(InputIo.PickupBoltFasten, false);
                }
            }
        };
        state.RepeatEnabled = true;
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.RepeatCycles >= 2 || state.IsError, TimeSpan.FromSeconds(55)),
                $"Cycles={state.Display.RepeatCycles}; Phase={state.Display.RepeatPhase}; Main={state.Display.ConveyorState}; {state.AlarmDetail}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(state.Display.RepeatCycles >= 2);
            Assert.True(shootingStarts >= 4);
            Assert.True(pickupDescents >= 4);
            Assert.True(pickupStarts >= 4);
            Assert.True(pickupAttempts >= 4);
            Assert.True(handoffTrips >= 4);
            Assert.True(completed.Count >= 2);
            Assert.Empty(forbidden);
            foreach (var assemblies in completed.Values)
            {
                Assert.Equal(2, assemblies.Length);
                foreach (var assembly in assemblies)
                {
                    Assert.Equal(BoltResultSource.IoAssumedOk, Assert.Single(assembly.PcbBoltResults).Value.Source);
                    Assert.Equal(BoltResultSource.IoAssumedOk, Assert.Single(assembly.PickupBoltResults).Value.Source);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatReturnsFromLastEnabledNgUnitWithoutRunningTheNgConveyor(bool shuttleEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = shuttleEnabled;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (!shuttleEnabled)
        {
            io.SetInput(InputIo.NgShuttleUp, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        }
        io.SetInputs(
            (InputIo.InspectionHeatSink1Present, true),
            (InputIo.InspectionHeatSink2Present, true));
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        state.RepeatEnabled = true;
        var shuttleOutputs = new ConcurrentQueue<bool>();
        var placedAndReleased = false;
        var pickedBackUp = false;
        var loweredWhileHolding = false;
        var openedAtShuttle = false;
        var ngConveyorRan = false;
        var stoppedForConfiguration = false;
        void CheckPickup()
        {
            if (gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition)
                && pickup.Lift == NgTransferLiftState.Down
                && pickup.Gripper == NgTransferGripperState.Closed
                && pickup.CarrierDetected)
            {
                loweredWhileHolding = true;
            }

            if (gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition)
                && io.GetInput(InputIo.NgShuttleCarrierDetected)
                && pickup.IsRaised
                && pickup.Gripper == NgTransferGripperState.Open
                && !pickup.CarrierDetected)
            {
                placedAndReleased = true;
            }

            if (placedAndReleased && pickup.CarrierDetected)
                pickedBackUp = true;
        }

        pickup.Changed += CheckPickup;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierGripperClose && !on
                && gantry.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition))
                openedAtShuttle = true;
            if (output == OutputIo.NgShuttleDown)
            {
                shuttleOutputs.Enqueue(on);
                if (on && !stoppedForConfiguration)
                {
                    stoppedForConfiguration = true;
                    machine.Stop();
                }
            }
            if (output == OutputIo.NgConveyorRun && on)
                ngConveyorRan = true;
        };
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        await WaitUntilAsync(() => state.Display.IsStartAllowed);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            if (shuttleEnabled)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(stoppedForConfiguration);
                Assert.Equal(new[] { true }, shuttleOutputs.ToArray());
                return;
            }

            Assert.True(await VirtualTest.WaitUntilAsync(
                () => state.Display.RepeatCycles >= 1 || state.IsError,
                TimeSpan.FromSeconds(10)),
                $"Phase={state.Display.RepeatPhase}, Alarm={state.AlarmDetail}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.Equal(1, state.Display.RepeatCycles);
            Assert.True(loweredWhileHolding);
            Assert.Equal(shuttleEnabled, openedAtShuttle);
            Assert.Equal(shuttleEnabled, placedAndReleased);
            Assert.Equal(shuttleEnabled, pickedBackUp);
            Assert.False(ngConveyorRan);
            Assert.Equal(shuttleEnabled ? new[] { true, false } : [], shuttleOutputs.ToArray());
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            pickup.Changed -= CheckPickup;
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
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
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
            await WaitUntilAsync(() => state.Display.IsStartAllowed);
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
            await WaitUntilAsync(() => state.Display.IsStartAllowed == !repeat);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task RepeatRejectsTwoOccupiedStationsAndAcceptsOneWithOnlyHeatSink2()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        await using var services = CreateServices(settings);
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
                (InputIo.BoltFasteningHeatSink2Present, true));
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
            Assert.Contains("one carrier", state.AlarmMessage);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

            io.SetInputs(
                (InputIo.PcbPlacementHeatSink2Present, false),
                (InputIo.BoltFasteningHeatSink2Present, false));
            await machine.ResetAsync();
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await services.GetRequiredService<PcbPlacementWork>().Station.SeatAsync(CancellationToken.None);
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

    [Fact]
    [Trait("Category", "MachineFlow")]
    public async Task RepeatMainReturnStopsWhenNgPickupDrops()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
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
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
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
            Assert.True(returned, $"Phase: {state.Display.RepeatPhase}; {state.AlarmDetail}");
            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(0, state.Display.RepeatCycles);
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
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
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
            await WaitUntilAsync(() => state.Display.RepeatPhase == RepeatPhase.Automatic);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            await machine.StartAsync(timeout.Token).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(0, state.Display.RepeatCycles);
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
        settings.Units.NgCarrierTransfer = true;
        settings.Units.NgShuttle = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var visited = new ConcurrentDictionary<InputIo, int>();
        var forbidden = new ConcurrentQueue<OutputIo>();
        var ngReverse = 0;
        var mainReverse = 0;
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
            if (!on)
                return;
            if (output is OutputIo.MainConveyorReadyToFront2
                or OutputIo.MainConveyorAvailableToRear
                or OutputIo.ShootBolt
                or OutputIo.ShootingFeederRunSignal)
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
                    () => state.Display.RepeatCycles >= 2 || state.IsError,
                    TimeSpan.FromSeconds(22)),
                $"Repeat timed out. Cycles={state.Display.RepeatCycles}, Phase={state.Display.RepeatPhase}, Main={state.Display.ConveyorState}, Alarm={state.AlarmMessage}");
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(state.Display.RepeatCycles >= 2, state.AlarmDetail);
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
            Assert.Empty(services.GetRequiredService<PcbPlacementWork>().Assemblies);
            Assert.Empty(services.GetRequiredService<BoltFasteningWork>().Assemblies);
            Assert.Empty(services.GetRequiredService<InspectionWork>().Assemblies);
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
}
