using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class NgConveyorTests
{
    [Fact]
    public async Task NgLineStoresRearFirstEjectsAndCompacts()
    {
        var io = CreateIo();
        IIoService signals = io;
        var work = new InspectionWork(
            ConveyorStation.Inspection(io),
            new TestInspectionGantryClearance(io));
        var settings = new NgConveyorSettings
        {
            AlarmCarrierCount = 2,
            TransferSpeed = 10_000,
            CarrierPickupPosition = new AxisPosition { X = 5, Y = 5 },
            ShuttlePlacePosition = new AxisPosition { X = 15, Y = 5 },
        };
        using var motion = new VirtualMotionService(
            new MotionSettings { HorizontalSpeed = 10_000 },
            new OperationCancellation(),
            hasZ: false,
            xRange: (0, 20),
            yRange: (0, 10));
        var machine = new VirtualMachine(io, [motion]);
        motion.PositionChanged += (x, y, _) =>
            machine.UpdateInspectionPosition(
                x,
                y,
                settings.CarrierPickupPosition,
                settings.ShuttlePlacePosition);
        var gantry = new InspectionGantry(motion);
        var line = new NgConveyorLine(
            io,
            work,
            new NgCarrierTransfer(io, gantry, settings),
            settings,
            inspectionBypassToNg: false);

        io.Initialize();
        motion.Initialize();
        using var cancellation = new CancellationTokenSource();
        var run = line.RunAsync(cancellation.Token);

        await SendNgCarrierAsync(io, work);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition1Occupied,
            true);

        Assert.Equal(1, line.CarrierCount);
        Assert.False(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));

        await SendNgCarrierAsync(io, work);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition2Occupied,
            true);
        await WaitForOutputAsync(io, OutputIo.Buzzer, true);

        Assert.Equal(2, line.CarrierCount);
        Assert.True(line.AlarmRequired);
        Assert.True(io.GetOutput(OutputIo.NgCarrierEjectLamp));
        Assert.False(io.GetInput(InputIo.NgConveyorPosition3Occupied));

        await SendNgCarrierAsync(io, work);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        Assert.True(line.Full);
        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));

        io.SetInput(InputIo.NgCarrierEjectButton, true);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            true);
        await signals.WaitForInputAsync(
            InputIo.NgConveyorPosition3Occupied,
            false);

        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(io.GetInput(InputIo.NgConveyorPosition2Occupied));
        Assert.Equal(2, line.CarrierCount);
        Assert.False(io.GetOutput(OutputIo.Buzzer));

        io.SetInput(InputIo.NgCarrierEjectCompleteButton, true);
        await WaitForOutputAsync(
            io,
            OutputIo.NgCarrierEjectCompleteLamp,
            false);
        io.SetInput(InputIo.NgCarrierEjectButton, false);
        io.SetInput(InputIo.NgCarrierEjectCompleteButton, false);
        await WaitForOutputAsync(io, OutputIo.Buzzer, true);

        Assert.True(line.AlarmRequired);
        Assert.True(io.GetOutput(OutputIo.NgCarrierEjectLamp));

        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task NgLineResumesTheSelectedPositionAfterStop()
    {
        var io = CreateIo();
        IIoService signals = io;
        var settings = new NgConveyorSettings();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            new OperationCancellation(),
            hasZ: false);
        var gantry = new InspectionGantry(motion);
        var line = new NgConveyorLine(
            io,
            new InspectionWork(
                ConveyorStation.Inspection(io),
                new TestInspectionGantryClearance(io)),
            new NgCarrierTransfer(io, gantry, settings),
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

        using var resumed = new CancellationTokenSource();
        var run = line.RunAsync(resumed.Token);
        await WaitForOutputAsync(io, OutputIo.NgConveyorRun, true);
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);

        resumed.Cancel();
        await run;

        Assert.True(io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
    }

    private static VirtualIoService CreateIo()
    {
        var outputs = new IoHardwareSettings[]
            {
                new ConveyorHardwareSettings(),
                new NgCarrierTransferHardwareSettings(),
                new NgShuttleHardwareSettings(),
                new NgConveyorHardwareSettings(),
                new MachineHardwareSettings(),
            }
            .SelectMany(settings => settings.Outputs)
            .ToDictionary();
        return new VirtualIoService(outputs, new MachineOptions());
    }

    private static async Task SendNgCarrierAsync(
        VirtualIoService io,
        InspectionWork work)
    {
        IIoService signals = io;
        await signals.SetOutputAndWaitAsync(
            OutputIo.InspectionBackupPlateUp,
            true);
        io.SetInput(InputIo.InspectionHeatSink1Present, false);
        io.SetInput(InputIo.InspectionHeatSink2Present, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        work.Complete();

        await signals.WaitForInputAsync(
            InputIo.InspectionCarrierPresent,
            false);
        await signals.WaitForInputAsync(InputIo.NgShuttleUp, true);
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
}
