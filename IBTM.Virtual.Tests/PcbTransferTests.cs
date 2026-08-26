using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class PcbTransferTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public async Task PlacementFillsOnlyPresentHousings(
        bool housing1,
        bool housing2,
        int expectedPlacements)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = MotionSettings(),
            RotationZ = 0,
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = MotionSettings(),
            BufferEntryZ = 0,
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var supplyRecipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
        };
        var placementRecipe = new PcbPlacementRecipe
        {
            Housing1PcbPlacementPosition = Position(70, 20, 10),
            Housing2PcbPlacementPosition = Position(80, 20, 10),
        };
        var virtualIo = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        IIoService io = virtualIo;
        var machine = new VirtualMachine(virtualIo);
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(
            placementSettings.Motion,
            operations);

        supplyMotion.PositionChanged += (x, y, z) =>
            machine.UpdateSupplyPosition(
                x,
                y,
                z,
                supplySettings.CarrierY,
                (supplyRecipe.Pcb1PickPosition.X,
                    supplyRecipe.Pcb1PickPosition.Z),
                (supplyRecipe.Pcb2PickPosition.X,
                    supplyRecipe.Pcb2PickPosition.Z),
                supplySettings.BufferHandoffPosition);
        placementMotion.PositionChanged += (x, y, z) =>
            machine.UpdatePlacementPosition(
                x,
                y,
                z,
                placementSettings.BufferHandoffPosition);

        var placementHandler = new PcbPlacementHandler(
            placementMotion,
            io,
            placementSettings);
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            io,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => placementSettings.BufferEntryZ);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings);
        var supply = new PcbSupplyProcess(
            supplyHandler,
            buffer);
        var placementWork = new PcbPlacementWork(io);
        var placement = new PcbPlacementProcess(
            buffer,
            placementHandler,
            placementWork);
        var placementCount = 0;
        var placementReleased = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector && !value)
            {
                placementReleased = true;
            }
            else if (output == OutputIo.PcbPlacementHandlerDown
                     && !value
                     && placementReleased)
            {
                placementReleased = false;
                Interlocked.Increment(ref placementCount);
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await HomeAsync(supplyMotion);
        await HomeAsync(placementMotion);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateUp, true);
        virtualIo.SetInput(InputIo.PcbPlacementCarrierJigPresent, true);
        virtualIo.SetInput(
            InputIo.PcbPlacementHousing1Present,
            housing1);
        virtualIo.SetInput(
            InputIo.PcbPlacementHousing2Present,
            housing2);

        using var cancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(supplyRecipe, cancellation.Token);
        var placementRun = placement.RunAsync(
            placementRecipe,
            cancellation.Token);

        var completed = await WaitUntilAsync(
            () => placementCount == expectedPlacements
                  && placementWork.Completed,
            TimeSpan.FromSeconds(15));

        cancellation.Cancel();
        await Task.WhenAll(supplyRun, placementRun);

        Assert.True(completed);
        Assert.Equal(expectedPlacements, placementCount);
        Assert.True(placementWork.Completed);
        Assert.Equal(expectedPlacements, placementWork.Assemblies.Count);
        Assert.Equal(
            housing1,
            placementWork.Assemblies.Any(
                assembly => assembly.Housing == HousingSlot.Housing1));
        Assert.Equal(
            housing2,
            placementWork.Assemblies.Any(
                assembly => assembly.Housing == HousingSlot.Housing2));
    }

    [Fact]
    public async Task SupplyResumesBufferExitBeforeStartingTheNextCarrier()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = MotionSettings(),
            RotationZ = 0,
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = MotionSettings(),
            BufferEntryZ = 0,
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var virtualIo = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(
            placementSettings.Motion,
            operations);
        var placementHandler = new PcbPlacementHandler(
            placementMotion,
            virtualIo,
            placementSettings);
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            virtualIo,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => placementSettings.BufferEntryZ);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            virtualIo,
            supplySettings);
        var supply = new PcbSupplyProcess(supplyHandler, buffer);
        var pcb1Visited = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            if (x < 40)
            {
                virtualIo.SetInput(InputIo.PcbSupplyPcbDetected, false);
            }

            if (Math.Abs(x - 10) <= 0.05
                && Math.Abs(y - 30) <= 0.05
                && Math.Abs(z - 5) <= 0.05)
            {
                pcb1Visited = true;
            }
        };

        virtualIo.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await HomeAsync(supplyMotion);
        await HomeAsync(placementMotion);
        await supplyMotion.MoveToAsync(50, 10, 8);
        await placementMotion.MoveToAsync(50, 10, 8);

        virtualIo.SetInput(InputIo.PcbSupplyUnrotated, false);
        virtualIo.SetInput(InputIo.PcbSupplyRotated, true);
        virtualIo.SetInput(InputIo.PcbSupplyPcbDetected, true);
        virtualIo.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        virtualIo.SetInput(InputIo.PcbBufferPcbPresent, true);
        virtualIo.SetInput(InputIo.PcbPlacementPcbDetected, true);
        virtualIo.SetInput(InputIo.PcbPlacementVacuumDetected, true);
        virtualIo.SetInput(InputIo.PcbPlacementIpmGripperOpen, false);
        virtualIo.SetInput(InputIo.PcbPlacementIpmGripperClosed, true);

        using var cancellation = new CancellationTokenSource();
        var run = supply.RunAsync(
            new PcbSupplyRecipe
            {
                Pcb1PickPosition = new() { X = 10, Z = 5 },
                Pcb2PickPosition = new() { X = 20, Z = 5 },
            },
            cancellation.Token);
        var exited = await WaitUntilAsync(
            () => !buffer.SupplyInside
                  && supplyHandler.Rotation == PcbSupplyRotation.Unrotated
                  && pcb1Visited,
            TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await run;

        Assert.True(exited);
        Assert.Equal(PcbSupplyCylinderState.Backward, supplyHandler.Nest);
        Assert.Equal(PcbSupplyCylinderState.Backward, supplyHandler.IpmFixer);
    }

    private static MotionSettings MotionSettings() => new()
    {
        HorizontalSpeed = 2_000,
        ZSpeed = 2_000,
    };

    private static VirtualMotionService Motion(
        MotionSettings settings,
        OperationCancellation operations) =>
        new(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            horizontalZ: () => 0,
            operationCancellation: operations);

    private static AxisPos Position(double x, double y, double z) =>
        new() { X = x, Y = y, Z = z };

    private static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings) =>
        settings
            .SelectMany(section => section.Outputs)
            .ToDictionary();

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 2_000);
        await motion.HomeAsync(MotionAxis.X, 2_000);
        await motion.HomeAsync(MotionAxis.Y, 2_000);
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started >= timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}
