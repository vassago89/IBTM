using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.BoltFastening;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Fact]
    public async Task RepeatMainOnlyReturnsFromStation3WithoutNg()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        var reachedStation3 = false;
        io.InputChanged += (input, on) =>
        {
            if (input == InputIo.InspectionHeatSink2Present && on)
                reachedStation3 = true;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var returned = false;
        io.OutputChanged += (output, on) =>
        {
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
            Assert.True(returned);
            Assert.True(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
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
        StationWork work = unit == MachineUnit.BoltFastening
            ? services.GetRequiredService<BoltFasteningWork>() : services.GetRequiredService<InspectionWork>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(unit == MachineUnit.BoltFastening
            ? InputIo.BoltFasteningHeatSink1Present : InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
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
            if (unit == MachineUnit.BoltFastening)
                Assert.False(services.GetRequiredService<BoltFasteningStation>().HasPendingResult);
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

    [Fact]
    public async Task NgRepeatWithoutMainReturnsFromPosition1ToShuttle()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgShuttle);
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
        PlacementHolding,
    }

    [Theory]
    [InlineData(PcbRepeatStopPoint.None)]
    [InlineData(PcbRepeatStopPoint.SupplyReturning)]
    [InlineData(PcbRepeatStopPoint.BothHolding)]
    [InlineData(PcbRepeatStopPoint.PlacementHolding)]
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
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var returns = 0;
        var reverseHandoffs = 0;
        var mainReturned = false;
        var enteredDisabledStation = false;
        var unsafeRelease = false;
        var descendedToSourceSlot = false;
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
            if (!on && output == OutputIo.PcbPlacementVacuumEjector && placement.IsAtReceivePosition())
            {
                reverseHandoffs++;
                unsafeRelease |= !supply.PcbSecured;
            }
        };
        void StopAtHandoff()
        {
            if (stopped || stopPoint == PcbRepeatStopPoint.None)
                return;
            var reached = stopPoint switch
            {
                PcbRepeatStopPoint.SupplyReturning => reverseHandoffs == 2 && supply.PcbSecured
                    && supplier.State == PcbSupplyState.MovingToPickup && supply.Feedback.IsMovingHorizontal
                    && supply.Feedback.GetPosition().X < settings.PcbSupply.HandoffPosition.X - 1
                    && supply.Feedback.GetPosition().X > recipe.PcbSupply.Pcb2PickPosition.X + 1,
                PcbRepeatStopPoint.BothHolding => supply.PcbSecured && placement.PcbSecured
                    && placement.IsAtReceivePosition(),
                PcbRepeatStopPoint.PlacementHolding => reverseHandoffs > 0 && supply.PcbReleased
                    && placement.PcbSecured && placement.IsAtReceivePosition(),
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
        supply.Feedback.PositionChanged += (x, y, z) => StopAtHandoff();
        placement.Feedback.PositionChanged += (x, y, z) =>
        {
            if (placer.State is PcbPlacementState.WaitingForSupply or PcbPlacementState.WaitingForSupplyRelease
                && y > settings.PcbPlacementHandler.HandoffPosition.Y
                && y < recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y)
            {
                placementDepartedInY = true;
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.X, x);
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, z);
                Assert.NotEqual(PcbPlacementHandoff.Clear, placer.Handoff);
            }
        };
        supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: MovingToPickup ", StringComparison.Ordinal))
                Assert.Equal(recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y, placement.Feedback.GetPosition().Y);
        };
        supply.Feedback.StateChanged += () =>
        {
            if (supply.PcbSecured && supply.Rotation == PcbSupplyRotationState.Rotated
                && !supply.IsAtRotationZ())
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
                    $"Supply={supplier.State}, Placement={placer.State}, {state.AlarmDetail}");
                Assert.False(state.IsError, state.AlarmDetail);
                Assert.True(supply.PcbSecured || placement.PcbSecured);
                run = machine.StartAsync(timeout.Token);
            }
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => mainReturned || state.IsError, TimeSpan.FromSeconds(27)),
                $"Supply={supplier.State}, Placement={placer.State}, Phase={machine.RepeatDisplayPhase}, {state.AlarmDetail}");
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(0, returns);
            Assert.False(descendedToSourceSlot);
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
