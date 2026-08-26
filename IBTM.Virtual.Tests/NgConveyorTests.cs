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
    public async Task InspectionRoutesNgCarriersAndResumesAfterEjection()
    {
        var outputs = new IoHardwareSettings[]
            {
                new ConveyorHardwareSettings(),
                new InspectionGantryHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        var io = new VirtualIoService(outputs, new MachineOptions());
        IIoService signals = io;
        var machine = new VirtualMachine(io);
        var work = new InspectionWork(io);
        var operations = new OperationCancellation();
        var settings = new InspectionGantrySettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 10_000 },
            UpperLeftLocatingPin = new AxisPos { X = 0, Y = 0 },
            LowerRightLocatingPin = new AxisPos { X = 20, Y = 0 },
            NgCarrierJigPickupPosition = new AxisPos { X = 5, Y = 5 },
            NgShuttlePlacePosition = new AxisPos { X = 15, Y = 5 },
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
                settings.NgCarrierJigPickupPosition,
                settings.NgShuttlePlacePosition);
        var line = new NgConveyorLine(
            io,
            work,
            new NgCarrierTransfer(io, motion, settings));
        var inspection = new InspectionProcess(
            work,
            new BoltImageCapture(
                motion,
                new VirtualCamera(motion.GetPosition),
                new VirtualLightController(),
                settings,
                new LightingSettings()),
            new BoltPresenceInspector(
                new BoltInspectionSettings(),
                new VirtualBoltRecessSegmenter()));
        var conveyor = new MainConveyor(
            io,
            operations,
            new PcbPlacementWork(io),
            new BoltFasteningWork(io),
            work);
        BoltPoint[] bolts =
        [
            new()
            {
                Number = 1,
                Housing = HousingSlot.Housing1,
                X = 5,
                Y = 5,
            },
        ];

        io.Initialize();
        motion.Initialize();
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var offeredToRear = false;
        io.OutputChanged += (output, value) =>
            offeredToRear |= output == OutputIo.MainConveyorAvailableToRear
                             && value;
        using var cancellation = new CancellationTokenSource();
        var conveyorRun = conveyor.RunAsync(cancellation.Token);
        var inspectionRun = inspection.RunAsync(bolts, cancellation.Token);
        var ngRun = line.RunAsync(cancellation.Token);

        await SendNgCarrierAsync(io, hasHousing: true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition1Occupied,
            true);

        await SendNgCarrierAsync(io, hasHousing: true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition2Occupied,
            true);

        await SendNgCarrierAsync(io, hasHousing: true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(line.Full);
        Assert.Equal(NgConveyorState.Full, line.State);
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.False(io.GetInput(InputIo.NgCarrierJigDetected));
        Assert.False(offeredToRear);

        await PresentCarrierAsync(io, hasHousing: false);
        await WaitUntilAsync(() => work.Completed);

        Assert.True(work.HasNg);
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));
        Assert.False(io.GetInput(InputIo.NgCarrierJigDetected));
        Assert.False(offeredToRear);

        io.SetInput(InputIo.NgCarrierEjectButton, true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            false);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            true);

        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));
        Assert.True(io.GetInput(InputIo.InspectionCarrierJigPresent));

        io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            false);
        io.SetInput(InputIo.NgCarrierEjectButton, false);
        io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);

        await signals.WaitForInputAsync(
            InputIo.InspectionCarrierJigPresent,
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

        cancellation.Cancel();
        await Task.WhenAll(conveyorRun, inspectionRun, ngRun);
    }

    private static async Task SendNgCarrierAsync(
        VirtualIoService io,
        bool hasHousing)
    {
        await PresentCarrierAsync(io, hasHousing);

        await ((IIoService)io).WaitForInputAsync(
            InputIo.InspectionCarrierJigPresent,
            false);
        await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleUp, true);
    }

    private static async Task PresentCarrierAsync(
        VirtualIoService io,
        bool hasHousing)
    {
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.InspectionBackupPlateUp,
            true);
        io.SetInput(InputIo.InspectionHousing1Present, hasHousing);
        io.SetInput(InputIo.InspectionHousing2Present, false);
        io.SetInput(InputIo.InspectionCarrierJigPresent, true);
    }

    private static async Task WaitForOutputAsync(
        VirtualIoService io,
        OutputIo output,
        bool value)
    {
        while (io.GetOutput(output) != value)
        {
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(10);
        }
    }
}
