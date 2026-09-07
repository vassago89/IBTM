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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArrivedCarrierIsNotLoweredAgainAfterRestart(bool toInspection)
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(io,
            placementEnabled: toInspection,
            boltFasteningEnabled: !toInspection);
        io.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        await SetSeatedCarrierAsync(io, io,
            toInspection ? InputIo.BoltFasteningCarrierPresent : InputIo.PcbPlacementCarrierPresent,
            toInspection ? OutputIo.BoltFasteningBackupPlateUp : OutputIo.PcbPlacementBackupPlateUp);
        var plateUp = toInspection
            ? InputIo.InspectionBackupPlateUp : InputIo.BoltFasteningBackupPlateUp;
        var plateOutput = toInspection
            ? OutputIo.InspectionBackupPlateUp : OutputIo.BoltFasteningBackupPlateUp;
        var stopperDown = toInspection
            ? InputIo.InspectionStopperDown : InputIo.BoltFasteningStopperDown;
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        void StopAtPlate(InputIo input, bool value)
        {
            if (input == plateUp && value)
            {
                firstStop.Cancel();
            }
        }

        io.InputChanged += StopAtPlate;
        await conveyor.RunAsync(firstStop.Token);
        io.InputChanged -= StopAtPlate;
        Assert.True(io.GetInput(plateUp));
        var loweredAgain = false;
        io.OutputChanged += (output, value) => loweredAgain |= output == plateOutput && !value;
        using var resumed = new CancellationTokenSource();
        var run = conveyor.RunAsync(resumed.Token);
        var seated = await WaitUntilAsync(
            () => io.GetInput(plateUp) && io.GetInput(stopperDown) && !conveyor.RunCommandOn,
            TimeSpan.FromSeconds(3));
        resumed.Cancel();
        await run;

        Assert.True(seated);
        Assert.False(loweredAgain);
    }

    [Fact]
    public void HeatSinkInputChangesDoNotEraseCompletionOrTransferredNg()
    {
        var io = CreateIo();
        var source = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var destination = new InspectionWork(
            ConveyorStation.Inspection(io), new TestNgCarrierTransferFeedback(io));
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = source.Assembly(HeatSinkSlot.HeatSink1);
        var result = new BoltResult(false, 1.25);
        assembly.RecordPcbBolt(1, result);
        source.Complete();
        var changes = 0;
        source.Changed += () => changes++;

        io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        Assert.Equal(2, changes);
        Assert.True(source.Completed);
        Assert.True(source.HasNg);
        Assert.Same(result, assembly.PcbBoltResults[1]);

        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, true);
        source.TransferAssembliesTo(destination);

        Assert.Empty(source.Assemblies);
        Assert.Same(assembly, Assert.Single(destination.Assemblies));
        Assert.False(destination.Completed);
        Assert.True(destination.HasNg);
        assembly.CompleteInspection();
        destination.Complete();
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        Assert.True(destination.Completed);
        Assert.True(destination.HasNg);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        Assert.Empty(destination.Assemblies);
        Assert.False(destination.Completed);
        Assert.False(destination.HasNg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringMotorSetupCannotTurnRunBackOn(bool manual)
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(io);
        io.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var stoppedDuringSetup = false;
        var started = false;
        io.OutputChanged += (output, value) =>
        {
            started |= output == OutputIo.MainConveyorRun && value;
            if (output == OutputIo.MainConveyorNormalSpeed && value)
            {
                stoppedDuringSetup = true;
                stop.Cancel();
            }
        };

        if (manual)
        {
            Assert.ThrowsAny<OperationCanceledException>(() => conveyor.RunMotor(stop.Token));
        }
        else
        {
            await conveyor.RunAsync(stop.Token);
        }
        Assert.True(stoppedDuringSetup);
        Assert.False(started);
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task NgCarrierWaitsAtInspectionForNgRemoval()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestNgCarrierTransferFeedback(io));
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            inspectionWork,
            routeInspectionToNg: () => inspectionWork.RouteToNg);
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
            InputIo.InspectionBackupPlateUp,
            true);
        assembly.CompleteInspection();
        inspectionWork.Complete();
        await Task.Delay(100);

        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.Same(assembly, Assert.Single(inspectionWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
        Assert.True(inspectionWork.HasNg);
        Assert.False(frontReadyBeforeTransfer);
        Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task DisabledNgTransferLetsNgCarrierLeaveAtRear()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestNgCarrierTransferFeedback(io));
        var ngTransferEnabled = true;
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            new PcbPlacementWork(
                ConveyorStation.PcbPlacement(io),
                isEnabled: () => false),
            new BoltFasteningWork(
                ConveyorStation.BoltFastening(io),
                isEnabled: () => false),
            inspectionWork,
            routeInspectionToNg: () => ngTransferEnabled && inspectionWork.RouteToNg);
        var discharged = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExit = false;
        io.InputChanged += (input, value) =>
        {
            if (input != InputIo.MainConveyorExitCarrierDetected)
            {
                return;
            }

            reachedExit |= value;
            if (reachedExit && !value)
            {
                discharged.TrySetResult();
            }
        };

        io.Initialize();
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.InspectionCarrierPresent,
            OutputIo.InspectionBackupPlateUp);
        var assembly = inspectionWork.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, false);
        assembly.CompleteInspection();
        inspectionWork.Complete();
        Assert.True(inspectionWork.HasNg);
        virtualIo.SetInput(InputIo.MainConveyorReadyFromRear, true);
        Assert.NotEqual(
            MainConveyorState.DischargingInspectionCarrier,
            conveyor.State);
        ngTransferEnabled = false;
        Assert.Equal(
            MainConveyorState.DischargingInspectionCarrier,
            conveyor.State);

        using var cancellation = new CancellationTokenSource();
        var run = conveyor.RunAsync(cancellation.Token);
        await discharged.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await run;

        Assert.False(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task BlockedRearDischargeDoesNotBlockFrontReceiving()
    {
        var virtualIo = CreateIo();
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
        var virtualIo = CreateIo();
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
    public async Task MainConveyorDoesNotInferCarrierFromCylinderPositions()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        _ = new VirtualMachine(virtualIo, []);
        var conveyor = CreateConveyor(io);

        io.Initialize();
        virtualIo.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await Task.WhenAll(
            io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementBackupPlateUp,
                false),
            io.SetOutputAndWaitAsync(
                OutputIo.PcbPlacementStopperUp,
                false),
            io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningBackupPlateUp,
                false),
            io.SetOutputAndWaitAsync(
                OutputIo.BoltFasteningStopperUp,
                true));

        Assert.Equal(MainConveyorState.WaitingForFrontCarrier, conveyor.State);
        Assert.False(conveyor.RunCommandOn);
    }

    [Fact]
    public async Task MainConveyorResumesAfterPassingEntrySensor()
    {
        var virtualIo = CreateIo();
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
            new InspectionWork(
                ConveyorStation.Inspection(io), new TestNgCarrierTransferFeedback(io)),
            routeInspectionToNg: () => false);

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
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var inspectionWork = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestNgCarrierTransferFeedback(io));
        var conveyor = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            inspectionWork,
            routeInspectionToNg: () => false);

        io.Initialize();
        await SetSeatedCarrierAsync(
            virtualIo,
            io,
            InputIo.InspectionCarrierPresent,
            OutputIo.InspectionBackupPlateUp);
        virtualIo.SetInput(InputIo.InspectionHeatSink1Present, true);
        inspectionWork.Assembly(HeatSinkSlot.HeatSink1).CompleteInspection();
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

    [Fact]
    public void DestinationSensorDoesNotMoveWorkWithoutTransfer()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var boltWork = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        _ = new MainConveyor(
            io,
            new OperationCancellation(),
            placementWork,
            boltWork,
            new InspectionWork(
                ConveyorStation.Inspection(io), new TestNgCarrierTransferFeedback(io)),
            routeInspectionToNg: () => false);

        io.Initialize();
        var assembly = placementWork.Assembly(HeatSinkSlot.HeatSink1);
        virtualIo.SetInput(InputIo.BoltFasteningCarrierPresent, true);

        Assert.Same(assembly, Assert.Single(placementWork.Assemblies));
        Assert.Empty(boltWork.Assemblies);
    }

    [Fact]
    public void StationWorkReadsCurrentUnitSetting()
    {
        var virtualIo = CreateIo();
        IIoService io = virtualIo;
        var enabled = false;
        var placementWork = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io), () => enabled);

        io.Initialize();
        virtualIo.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);

        Assert.True(placementWork.Completed);
        enabled = true;
        Assert.False(placementWork.Completed);
        enabled = false;
        Assert.True(placementWork.Completed);
    }

    [Fact]
    public void DisabledInspectionCanTransferCarrierAlreadyPresentAtStartup()
    {
        var io = CreateIo();
        io.Initialize();
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionStopperUp, false);
        io.SetInput(InputIo.InspectionStopperDown, true);

        var work = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestNgCarrierTransferFeedback(io),
            isEnabled: () => false);

        Assert.True(work.CarrierSeated);
        Assert.True(work.CanTransfer);
        Assert.True(work.HasNg);

        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        Assert.True(work.CanTransfer);
        Assert.False(work.HasNg);
        work.Assembly(HeatSinkSlot.HeatSink1)
            .RecordPcbBolt(1, new BoltResult(false, 1.25));
        Assert.True(work.HasNg);

        io.SetInput(InputIo.InspectionCarrierPresent, false);
        Assert.False(work.CanTransfer);
        Assert.False(work.HasNg);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VirtualCarrierContinuesAfterStopAtEndSensor(bool exiting)
    {
        var io = CreateIo();
        _ = new VirtualMachine(io, []);
        var conveyor = CreateConveyor(io, inspectionEnabled: !exiting);
        io.Initialize();
        if (exiting)
        {
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            await SetSeatedCarrierAsync(
                io,
                io,
                InputIo.InspectionCarrierPresent,
                OutputIo.InspectionBackupPlateUp);
        }

        var sensor = exiting
            ? InputIo.MainConveyorExitCarrierDetected
            : InputIo.MainConveyorEntryCarrierDetected;
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        void StopAtSensor(InputIo input, bool value)
        {
            if (input == sensor && value)
            {
                firstStop.Cancel();
            }
        }

        io.InputChanged += StopAtSensor;
        await conveyor.RunAsync(firstStop.Token);
        io.InputChanged -= StopAtSensor;
        Assert.True(io.GetInput(sensor));
        Assert.False(conveyor.RunCommandOn);

        using var resumed = new CancellationTokenSource();
        var run = conveyor.RunAsync(resumed.Token);
        var completed = await WaitUntilAsync(
            () => exiting
                ? !io.GetInput(sensor)
                : io.GetInput(InputIo.PcbPlacementCarrierPresent)
                  && io.GetInput(InputIo.PcbPlacementBackupPlateUp),
            TimeSpan.FromSeconds(3));
        resumed.Cancel();
        await run;

        Assert.True(completed);
        Assert.False(io.GetInput(sensor));
        Assert.False(conveyor.RunCommandOn);
    }

    private static MainConveyor CreateConveyor(
        IIoService io,
        bool placementEnabled = true,
        bool boltFasteningEnabled = true,
        bool inspectionEnabled = true) => new(
            io,
            new OperationCancellation(),
            new PcbPlacementWork(
                ConveyorStation.PcbPlacement(io),
                () => placementEnabled),
            new BoltFasteningWork(
                ConveyorStation.BoltFastening(io),
                () => boltFasteningEnabled),
            new InspectionWork(
                ConveyorStation.Inspection(io),
                new TestNgCarrierTransferFeedback(io),
                () => inspectionEnabled),
            routeInspectionToNg: () => false);

    private static VirtualIoService CreateIo() => new(
        Outputs(new ConveyorHardwareSettings(), new NgCarrierTransferHardwareSettings()),
        new MachineOptions());

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
