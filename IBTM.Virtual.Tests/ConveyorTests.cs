using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class ConveyorTests
{
    [Fact]
    public async Task MainConveyorMovesACompletedCarrierThroughEveryStation()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var placementWork = new PcbPlacementWork(io);
        var boltWork = new BoltFasteningWork(io);
        var inspectionWork = new InspectionWork(io);
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        var inspectionReleasedBeforeDischarge = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorRun
                && value
                && io.GetOutput(OutputIo.MainConveyorAvailableToRear))
            {
                inspectionReleasedBeforeDischarge =
                    io.GetInput(InputIo.InspectionBackupPlateDown)
                    && io.GetInput(InputIo.InspectionStopperDown);
            }
        };
        var run = conveyor.RunAsync(cancellation.Token);

        await io.WaitForInputAsync(
            InputIo.PcbPlacementBackupPlateUp,
            true);
        var assembly = placementWork.Assembly(HousingSlot.Housing1);
        conveyor.BypassWork(ConveyorStation.PcbPlacement);

        await io.WaitForInputAsync(
            InputIo.BoltFasteningBackupPlateUp,
            true);
        Assert.True(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.BoltFasteningHousing1Present));
        Assert.True(io.GetInput(InputIo.BoltFasteningHousing2Present));
        Assert.Same(assembly, Assert.Single(boltWork.Assemblies));
        await io.WaitForInputAsync(
            InputIo.PcbPlacementBackupPlateUp,
            true);
        Assert.False(placementWork.Completed);
        conveyor.BypassWork(ConveyorStation.BoltFastening);

        await io.WaitForInputAsync(
            InputIo.InspectionBackupPlateUp,
            true);
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionHousing1Present));
        Assert.True(io.GetInput(InputIo.InspectionHousing2Present));
        Assert.Same(assembly, Assert.Single(inspectionWork.Assemblies));
        await io.WaitForInputAsync(
            InputIo.BoltFasteningBackupPlateUp,
            true);
        Assert.False(boltWork.Completed);
        conveyor.BypassWork(ConveyorStation.Inspection);

        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorAvailableToRear,
            true);
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);

        await io.WaitForInputAsync(
            InputIo.InspectionCarrierJigPresent,
            false);
        Assert.False(io.GetInput(InputIo.InspectionHousing1Present));
        Assert.False(io.GetInput(InputIo.InspectionHousing2Present));
        await io.WaitForInputAsync(
            InputIo.InspectionBackupPlateUp,
            true);
        Assert.False(inspectionWork.Completed);
        Assert.True(inspectionReleasedBeforeDischarge);
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task NgCarrierWaitsAtInspectionForNgRemoval()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var placementWork = new PcbPlacementWork(io);
        var boltWork = new BoltFasteningWork(io);
        var inspectionWork = new InspectionWork(io);
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierJigPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        var assembly = boltWork.Assembly(HousingSlot.Housing1);
        assembly.RecordPcbBolt(1, new BoltResult(false, 0));
        boltWork.Complete();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);

        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(
            InputIo.InspectionBackupPlateUp,
            true);
        inspectionWork.Complete();
        await Task.Delay(100);

        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.Same(assembly, Assert.Single(inspectionWork.Assemblies));
        Assert.True(inspectionWork.HasNg);
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task BlockedRearDischargeDoesNotBlockFrontReceiving()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var conveyor = CreateConveyor(io);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateDown, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateUp, true);
        virtualIo.SetInput(InputIo.InspectionHousing1Present, true);
        virtualIo.SetInput(InputIo.InspectionCarrierJigPresent, true);
        var bothSmemaOutputsOn = false;
        io.OutputChanged += (_, _) => bothSmemaOutputsOn |=
            io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && io.GetOutput(OutputIo.MainConveyorAvailableToRear);

        var run = conveyor.RunAsync(cancellation.Token);
        conveyor.BypassWork(ConveyorStation.Inspection);
        await io.WaitForInputAsync(
            InputIo.PcbPlacementBackupPlateUp,
            true);
        await io.WaitForInputAsync(
            InputIo.PcbPlacementStopperDown,
            true);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorAvailableToRear,
            true);

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.True(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
        Assert.False(bothSmemaOutputsOn);

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task MainConveyorMovesTheRearMostRunnableCarrierFirst()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var conveyor = CreateConveyor(io);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.PcbPlacementCarrierJigPresent,
            OutputIo.PcbPlacementBackupPlateUp);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierJigPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        var run = conveyor.RunAsync(cancellation.Token);
        conveyor.BypassWork(ConveyorStation.PcbPlacement);
        conveyor.BypassWork(ConveyorStation.BoltFastening);
        await io.WaitForInputAsync(
            InputIo.InspectionCarrierJigPresent,
            true);

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.BoltFasteningCarrierJigPresent));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task InternalTransferDoesNotPulseFrontReady()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var conveyor = CreateConveyor(io);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierJigPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        conveyor.BypassWork(ConveyorStation.BoltFastening);
        var frontReadyBeforeTransfer = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorReadyToFront2
                && value
                && !io.GetInput(InputIo.InspectionCarrierJigPresent))
            {
                frontReadyBeforeTransfer = true;
            }
        };

        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(
            InputIo.InspectionCarrierJigPresent,
            true);

        Assert.False(frontReadyBeforeTransfer);

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task MainConveyorWaitsForSmemaAndStopsWhenCommanded()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 500 });
        IIoService io = virtualIo;
        var conveyor = CreateConveyor(io);

        io.Initialize();
        var run = conveyor.RunAsync();
        await Task.Delay(700);

        Assert.False(run.IsCompleted);
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            true);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);

        Assert.True(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.True(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        conveyor.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(run.IsCompleted);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.True(io.GetInput(InputIo.PcbPlacementBackupPlateDown));
    }

    [Fact]
    public async Task MainConveyorStopsWhenCarrierDetectionTimesOut()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 500 });
        IIoService io = virtualIo;
        var conveyor = CreateConveyor(io);

        io.Initialize();
        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            true);

        await Assert.ThrowsAsync<IoTimeoutException>(
            () => conveyor.RunAsync());

        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
    }

    [Fact]
    public async Task MainConveyorReceivesAndSecuresTheFirstCarrier()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo);
        var conveyor = CreateConveyor(io);
        using var cancellation = new CancellationTokenSource();
        var stopperRaisedBeforeReady = false;
        var stopperRaisedBeforeRun = false;

        io.Initialize();
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorReadyToFront2 && value)
            {
                stopperRaisedBeforeReady = io.GetInput(
                    InputIo.PcbPlacementStopperUp);
            }
            else if (output == OutputIo.MainConveyorRun && value)
            {
                stopperRaisedBeforeRun = io.GetInput(
                    InputIo.PcbPlacementStopperUp);
            }
        };
        var run = conveyor.RunAsync(cancellation.Token);

        await io.WaitForInputAsync(
            InputIo.PcbPlacementBackupPlateUp,
            true);
        await io.WaitForInputAsync(
            InputIo.PcbPlacementStopperDown,
            true);

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierJigPresent));
        Assert.True(stopperRaisedBeforeReady);
        Assert.True(stopperRaisedBeforeRun);
        Assert.True(io.GetInput(InputIo.PcbPlacementStopperDown));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        cancellation.Cancel();
        await run;
    }

    private static MainConveyor CreateConveyor(IIoService io) => new(
        io,
        new OperationCancellation(),
        new PcbPlacementWork(io),
        new BoltFasteningWork(io),
        new InspectionWork(io));

    private static async Task SetSeatedCarrierAsync(
        VirtualIoService virtualIo,
        IIoService io,
        InputIo carrier,
        OutputIo backupPlate)
    {
        await io.SetOutputAndWaitAsync(backupPlate, true);
        virtualIo.SetInput(carrier, true);
    }

    private static async Task WaitForOutputAsync(
        IIoService io,
        OutputIo output,
        bool value)
    {
        while (io.GetOutput(output) != value)
        {
            await Task.Delay(10);
        }
    }

}
