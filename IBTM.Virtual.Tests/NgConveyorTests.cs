using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class NgConveyorTests
{
    [Fact]
    public async Task NgLineStoresRearFirstAndResumesAfterEjectionStop()
    {
        var outputs = new IoHardwareSettings[]
            {
                new ConveyorHardwareSettings(),
                new InspectionGantryHardwareSettings(),
                new NgCarrierTransferHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
                new MachineHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        var io = new VirtualIoService(outputs, new MachineOptions());
        IIoService signals = io;
        var machine = new VirtualMachine(io, []);
        var work = new InspectionWork(
            io,
            new TestInspectionGantryClearance(io));
        var operations = new OperationCancellation();
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftPin = new AxisPos { X = 0, Y = 0 },
            LowerRightPin = new AxisPos { X = 20, Y = 0 },
        };
        var settings = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 10_000 },
        };
        var ngSettings = new NgConveyorSettings
        {
            AlarmCarrierCount = 2,
            TransferSpeed = 10_000,
            CarrierPickupPosition = new AxisPos { X = 5, Y = 5 },
            ShuttlePlacePosition = new AxisPos { X = 15, Y = 5 },
        };
        using var motion = new VirtualMotionService(
            settings.Motion,
            operations,
            hasZ: false,
            xRange: (0, 20),
            yRange: (0, 10));
        motion.PositionChanged += (x, y, _) =>
            machine.UpdateInspectionPosition(
                x,
                y,
                ngSettings.CarrierPickupPosition,
                ngSettings.ShuttlePlacePosition);
        var transfer = new NgCarrierTransfer(io, motion, ngSettings);
        var line = new NgConveyorLine(
            io,
            work,
            transfer,
            ngSettings,
            inspectionBypassToNg: false);
        var inspection = new InspectionProcess(
            work,
            new BoltImageCapture(
                motion,
                new VirtualCamera(motion.GetPosition, () => []),
                new VirtualLightController(),
                settings,
                carrierReference,
                new LightingSettings()),
            new BoltPresenceInspector(
                new BoltInspectionSettings(),
                new VirtualBoltRecessSegmenter()));
        var conveyor = new MainConveyor(
            io,
            operations,
            new PcbPlacementWork(io),
            new BoltFasteningWork(io),
            work,
            placementEnabled: true,
            boltFasteningEnabled: true,
            inspectionEnabled: true,
            inspectionBypassToNg: false);
        BoltPoint[] bolts =
        [
            new()
            {
                Number = 1,
                HeatSink = HeatSinkSlot.HeatSink1,
                X = 5,
                Y = 5,
            },
        ];

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var offeredToRear = false;
        var unsafeConveyorStart = false;
        var ejectRuns = 0;
        io.OutputChanged += (output, value) =>
        {
            offeredToRear |= output == OutputIo.MainConveyorAvailableToRear
                             && value;
            if (output != OutputIo.NgConveyorRun || !value)
            {
                return;
            }

            var stopperUp = io.GetInput(InputIo.NgConveyorStopperUp);
            var stopperDown = io.GetInput(InputIo.NgConveyorStopperDown);
            unsafeConveyorStart |= stopperUp == stopperDown;
            if (stopperDown)
            {
                Interlocked.Increment(ref ejectRuns);
            }
        };
        using var cancellation = new CancellationTokenSource();
        using var ngCancellation = new CancellationTokenSource();
        using var transferStop = new CancellationTokenSource();
        using var openingStop = new CancellationTokenSource();
        var stopAtShuttle = true;
        NgConveyorState? stateWhileOpening = null;
        io.InputChanged += (input, value) =>
        {
            if (stopAtShuttle
                && input == InputIo.NgCarrierPickupDown
                && value
                && transfer.AtShuttle)
            {
                stopAtShuttle = false;
                transferStop.Cancel();
            }
        };
        CancellationTokenSource? stopAfterEject = null;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgCarrierGripperClose
                && !value
                && stateWhileOpening is null)
            {
                io.SetInput(InputIo.NgCarrierGripperClosed, false);
                stateWhileOpening = line.State;
                openingStop.Cancel();
            }

            if (output == OutputIo.NgCarrierEjectCompleteLamp && value)
            {
                stopAfterEject?.Cancel();
            }
        };
        var conveyorRun = conveyor.RunAsync(cancellation.Token);
        var inspectionRun = inspection.RunAsync(bolts, cancellation.Token);
        var ngRun = line.RunAsync(transferStop.Token);

        await SendNgCarrierAsync(io, hasHeatSink: true);
        await ngRun;

        Assert.True(transfer.CarrierDetected);
        Assert.Equal(
            NgTransferGripperState.Closed,
            transfer.Gripper);
        Assert.Equal(NgTransferLiftState.Down, transfer.Lift);
        Assert.Equal(
            NgConveyorState.OpeningTransferGripper,
            line.State);

        ngRun = line.RunAsync(openingStop.Token);
        await ngRun;

        Assert.Equal(
            NgConveyorState.OpeningTransferGripper,
            stateWhileOpening);
        Assert.True(transfer.CarrierDetected);

        ngRun = line.RunAsync(ngCancellation.Token);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition1Occupied,
            true);
        await WaitForOutputAsync(io, OutputIo.NgConveyorRun, false);

        Assert.Equal(1, line.CarrierCount);
        Assert.False(line.AlarmRequired);
        Assert.False(io.GetOutput(OutputIo.Buzzer));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await SendNgCarrierAsync(io, hasHeatSink: true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition2Occupied,
            true);

        await WaitForOutputAsync(io, OutputIo.Buzzer, true);
        Assert.Equal(2, line.CarrierCount);
        Assert.True(line.AlarmRequired);
        Assert.True(io.GetOutput(OutputIo.NgCarrierEjectLamp));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await SendNgCarrierAsync(io, hasHeatSink: true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await WaitUntilAsync(() => line.State == NgConveyorState.Full);

        Assert.True(line.Full);
        Assert.Equal(NgConveyorState.Full, line.State);
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));
        Assert.False(offeredToRear);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await PresentCarrierAsync(io, hasHeatSink: false);
        await WaitUntilAsync(() => work.Completed);

        Assert.True(work.HasNg);
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));
        Assert.False(offeredToRear);

        stopAfterEject = ngCancellation;
        io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            true);
        Assert.False(io.GetOutput(OutputIo.Buzzer));
        await ngRun;

        Assert.False(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.Equal(1, ejectRuns);
        Assert.False(unsafeConveyorStart);
        stopAfterEject = null;
        using var resumedNgCancellation = new CancellationTokenSource();
        ngRun = line.RunAsync(resumedNgCancellation.Token);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            false);

        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));

        io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            false);
        io.SetInput(InputIo.NgCarrierEjectButton, false);
        io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        await WaitForOutputAsync(io, OutputIo.Buzzer, true);
        Assert.True(line.AlarmRequired);

        await signals.WaitForInputAsync(
            InputIo.InspectionCarrierPresent,
            false);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.True(line.Full);
        Assert.False(offeredToRear);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
        Assert.Equal(1, ejectRuns);
        Assert.False(unsafeConveyorStart);

        cancellation.Cancel();
        resumedNgCancellation.Cancel();
        await Task.WhenAll(conveyorRun, inspectionRun, ngRun);
    }

    [Fact]
    public async Task NgLineResumesTheSelectedPositionAfterStop()
    {
        var outputs = new IoHardwareSettings[]
            {
                new NgCarrierTransferHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        var io = new VirtualIoService(outputs, new MachineOptions());
        IIoService signals = io;
        var settings = new NgConveyorSettings();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasZ: false);
        var line = new NgConveyorLine(
            io,
            new InspectionWork(
                io,
                new TestInspectionGantryClearance(io)),
            new NgCarrierTransfer(io, motion, settings),
            settings,
            inspectionBypassToNg: false);

        io.Initialize();
        await signals.SetOutputAndWaitAsync(OutputIo.NgShuttleDown, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgConveyorPosition3Occupied, true);
        using var stop = new CancellationTokenSource();
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.NgConveyorRun && value)
            {
                stop.Cancel();
            }
        };

        await line.RunAsync(stop.Token);
        io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        io.SetInput(InputIo.NgConveyorPosition3Occupied, false);

        Assert.Equal(NgConveyorState.MovingToPosition1, line.State);

        using var resumed = new CancellationTokenSource();
        var run = line.RunAsync(resumed.Token);
        await WaitForOutputAsync(io, OutputIo.NgConveyorRun, true);
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
        await WaitUntilAsync(
            () => line.State == NgConveyorState.ReadyToEject);

        resumed.Cancel();
        await run;

        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
    }

    [Fact]
    public void NgLineDoesNotGuessAPositionAfterProgramRestart()
    {
        var outputs = new IoHardwareSettings[]
            {
                new NgCarrierTransferHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        var io = new VirtualIoService(outputs, new MachineOptions());
        var settings = new NgConveyorSettings();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasZ: false);

        io.Initialize();
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        var line = new NgConveyorLine(
            io,
            new InspectionWork(
                io,
                new TestInspectionGantryClearance(io)),
            new NgCarrierTransfer(io, motion, settings),
            settings,
            inspectionBypassToNg: false);

        Assert.Equal(
            NgConveyorState.CarrierBetweenPositions,
            line.State);
    }

    [Fact]
    public async Task NgLineRequiresANewEjectButtonPressAfterRestart()
    {
        var outputs = new IoHardwareSettings[]
            {
                new NgCarrierTransferHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        var io = new VirtualIoService(outputs, new MachineOptions());
        var settings = new NgConveyorSettings();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasZ: false);

        io.Initialize();
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.NgCarrierEjectButton, true);
        var line = new NgConveyorLine(
            io,
            new InspectionWork(
                io,
                new TestInspectionGantryClearance(io)),
            new NgCarrierTransfer(io, motion, settings),
            settings,
            inspectionBypassToNg: false);
        using var cancellation = new CancellationTokenSource();
        var run = line.RunAsync(cancellation.Token);

        await Task.Delay(50);

        Assert.Equal(
            NgConveyorState.WaitingForEjectButtonRelease,
            line.State);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        io.SetInput(InputIo.NgCarrierEjectButton, false);
        Assert.Equal(NgConveyorState.ReadyToEject, line.State);
        io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(io, OutputIo.NgConveyorRun, true);

        cancellation.Cancel();
        await run;
    }

    private static async Task SendNgCarrierAsync(
        VirtualIoService io,
        bool hasHeatSink)
    {
        await PresentCarrierAsync(io, hasHeatSink);

        await ((IIoService)io).WaitForInputAsync(
            InputIo.InspectionCarrierPresent,
            false);
        await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleUp, true);
    }

    private static async Task PresentCarrierAsync(
        VirtualIoService io,
        bool hasHeatSink)
    {
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.InspectionBackupPlateUp,
            true);
        io.SetInput(InputIo.InspectionHeatSink1Present, hasHeatSink);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
    }

    private static async Task WaitForOutputAsync(
        VirtualIoService io,
        OutputIo output,
        bool value)
    {
        var started = DateTime.UtcNow;
        while (io.GetOutput(output) != value)
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException(output.ToString());
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }
}
