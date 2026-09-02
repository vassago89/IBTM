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
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class ConveyorTests
{
    [Fact]
    public async Task NgCarrierWaitsAtInspectionForNgRemoval()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            null);
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork,
            placementEnabled: true,
            boltFasteningEnabled: true,
            inspectionEnabled: true,
            inspectionBypassToNg: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        var assembly = boltWork.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordPcbBolt(1, new BoltResult(false, 0));
        boltWork.Complete();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);

        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(
            InputIo.InspectionBackupPlateUp,
            true);
        inspectionWork.Complete();
        await Task.Delay(100);

        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.Same(assembly, Assert.Single(inspectionWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
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
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(
            io,
            inspectionEnabled: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            false);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateDown, false);
        virtualIo.SetInput(InputIo.InspectionBackupPlateUp, true);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        virtualIo.SetInput(InputIo.InspectionCarrierPresent, true);
        var bothSmemaOutputsOn = false;
        io.OutputChanged += (_, _) => bothSmemaOutputsOn |=
            io.GetOutput(OutputIo.MainConveyorReadyToFront2)
            && io.GetOutput(OutputIo.MainConveyorAvailableToRear);

        var run = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorAvailableToRear,
            true);

        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));

        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            true);
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

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierPresent));
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
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
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(
            io,
            placementEnabled: false,
            boltFasteningEnabled: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.PcbPlacementCarrierPresent,
            OutputIo.PcbPlacementBackupPlateUp);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(
            InputIo.InspectionCarrierPresent,
            true);

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierPresent));
        Assert.False(io.GetInput(InputIo.BoltFasteningCarrierPresent));

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
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(
            io,
            boltFasteningEnabled: false);
        using var cancellation = new CancellationTokenSource();

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        var frontReadyBeforeTransfer = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorReadyToFront2
                && value
                && !io.GetInput(InputIo.InspectionCarrierPresent))
            {
                frontReadyBeforeTransfer = true;
            }
        };

        var run = conveyor.RunAsync(cancellation.Token);
        await io.WaitForInputAsync(
            InputIo.InspectionCarrierPresent,
            true);

        Assert.False(frontReadyBeforeTransfer);

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task MainConveyorResumesAfterPassingEntrySensor()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            new InspectionWork(ConveyorStation.Inspection(io), null),
            placementEnabled: true,
            boltFasteningEnabled: true,
            inspectionEnabled: true,
            inspectionBypassToNg: false);

        io.Initialize();
        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            true);
        var firstRun = conveyor.RunAsync();
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);
        virtualIo.SetInput(
            InputIo.MainConveyorEntryCarrierDetected,
            true);
        virtualIo.SetInput(
            InputIo.MainConveyorEntryCarrierDetected,
            false);

        conveyor.Stop();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        virtualIo.SetInput(
            InputIo.MainConveyorAvailableFromFront2,
            false);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.BoltFasteningCarrierPresent,
            OutputIo.BoltFasteningBackupPlateUp);
        boltWork.Complete();

        using var cancellation = new CancellationTokenSource();
        var resumedRun = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);
        virtualIo.SetInput(
            InputIo.PcbPlacementCarrierPresent,
            true);
        await io.WaitForInputAsync(
            InputIo.PcbPlacementBackupPlateUp,
            true);
        cancellation.Cancel();
        await resumedRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(io.GetInput(InputIo.PcbPlacementCarrierPresent));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
    }

    [Fact]
    public async Task MainConveyorResumesBeforeReachingExitSensor()
    {
        var virtualIo = new VirtualIoService(
            new ConveyorHardwareSettings().Outputs,
            new MachineOptions());
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            null);
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            inspectionWork,
            placementEnabled: true,
            boltFasteningEnabled: true,
            inspectionEnabled: true,
            inspectionBypassToNg: false);

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.InspectionCarrierPresent,
            OutputIo.InspectionBackupPlateUp);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        inspectionWork.Complete();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        var firstRun = conveyor.RunAsync();
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);
        virtualIo.SetInput(InputIo.InspectionCarrierPresent, false);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, false);

        conveyor.Stop();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.PcbPlacementCarrierPresent,
            OutputIo.PcbPlacementBackupPlateUp);
        placementWork.Complete();
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, false);

        using var cancellation = new CancellationTokenSource();
        var resumedRun = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorAvailableToRear,
            true);
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);
        virtualIo.SetInput(
            InputIo.MainConveyorExitCarrierDetected,
            true);
        conveyor.Stop();
        await resumedRun.WaitAsync(TimeSpan.FromSeconds(2));

        var finalRun = conveyor.RunAsync(cancellation.Token);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            true);
        virtualIo.SetInput(
            InputIo.MainConveyorExitCarrierDetected,
            false);
        await WaitForOutputAsync(
            io,
            OutputIo.MainConveyorRun,
            false);

        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await finalRun.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static MainConveyor CreateConveyor(
        IIoService io,
        bool placementEnabled = true,
        bool boltFasteningEnabled = true,
        bool inspectionEnabled = true) => new(
            io,
            new OperationCancellation(),
            new PcbPlacementWork(ConveyorStation.PcbPlacement(io)),
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            new InspectionWork(ConveyorStation.Inspection(io), null),
            placementEnabled,
            boltFasteningEnabled,
            inspectionEnabled,
            inspectionBypassToNg: false);

    private static async Task SetSeatedCarrierAsync(
        VirtualIoService virtualIo,
        IIoService io,
        InputIo carrier,
        OutputIo backupPlate)
    {
        virtualIo.SetInput(carrier, true);
        await io.SetOutputAndWaitAsync(backupPlate, true);
    }

}
