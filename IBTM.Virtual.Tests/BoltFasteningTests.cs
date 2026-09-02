using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Fact]
    public async Task ShootingFeederKeepsTheNextBoltReady()
    {
        var io = new VirtualIoService(
            new BoltFeederHardwareSettings().Outputs,
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        var feeder = new ShootingBoltFeeder(
            io,
            new BoltFeederSettings
            {
                ShootingTimeoutMilliseconds = 500,
            });
        var refillCount = 0;
        var runCount = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingFeederRunSignal && value)
            {
                Interlocked.Increment(ref runCount);
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.ShootingFeederBoltDetected && value)
            {
                Interlocked.Increment(ref refillCount);
            }
        };

        io.Initialize();
        using var cancellation = new CancellationTokenSource();
        var run = feeder.RunAsync(cancellation.Token);
        var firstBolt = await WaitUntilAsync(
            () => runCount == 1
                  && feeder.State == BoltFeederState.BoltReady
                  && !io.GetOutput(OutputIo.ShootingFeederRunSignal),
            TimeSpan.FromSeconds(1));
        io.SetInput(InputIo.ShootingFeederBoltDetected, false);
        var nextBolt = await WaitUntilAsync(
            () => runCount == 2
                  && refillCount == 2
                  && feeder.State == BoltFeederState.BoltReady
                  && !io.GetOutput(OutputIo.ShootingFeederRunSignal),
            TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        await run;

        Assert.True(firstBolt);
        Assert.True(nextBolt);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
    }

    [Fact]
    public async Task BoltProcessRecordsNgWithoutSkippingRemainingBolts()
    {
        var operations = new OperationCancellation();
        var settings = new BoltFasteningSettings
        {
            Motion = new MotionSettings
            {
                HorizontalSpeed = 20_000,
                ZSpeed = 20_000,
            },
            SafeZ = 0,
            PickupPosition = new AxisPosition { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(
                new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io, []);
        var bus = new VirtualAdcBus();
        var connection = new HantasSettings { PortName = "Virtual" };
        var pickupHead = new AdcBoltHead(bus, connection, 1);
        var shootingHead = new AdcBoltHead(bus, connection, 2);
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
            LowerRightLocatingPin = new AxisPosition { X = 100, Y = 0 },
        };
        using var motion = new VirtualMotionService(
            settings.Motion,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            horizontalZ: () => settings.SafeZ,
            operationCancellation: operations);
        var station = new BoltFasteningStation(
            shootingHead,
            pickupHead,
            io,
            motion,
            settings,
            carrierReference);
        var work = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var feederSettings = new BoltFeederSettings();
        var pickupFeeder = new PickupBoltFeeder(io, feederSettings);
        var shootingFeeder = new ShootingBoltFeeder(io, feederSettings);
        var process = new BoltFasteningProcess(
            station,
            work,
            pickupFeeder,
            shootingFeeder);
        var recipe = new BoltFasteningRecipe
        {
            PcbPreset = 4,
            IpmSeatingPreset = 3,
            IpmFinalPreset = 5,
            BoltPoints =
            [
                Bolt(1, HeatSinkSlot.HeatSink1, FasteningHead.Pickup, 20, 30),
                Bolt(2, HeatSinkSlot.HeatSink1, FasteningHead.Shooting, 20, 30),
                Bolt(3, HeatSinkSlot.HeatSink2, FasteningHead.Pickup, 30, 40),
                Bolt(4, HeatSinkSlot.HeatSink2, FasteningHead.Shooting, 30, 40),
            ],
        };

        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        using var feederCancellation = new CancellationTokenSource();
        var feederRuns = Task.WhenAll(
            pickupFeeder.RunAsync(feederCancellation.Token),
            shootingFeeder.RunAsync(feederCancellation.Token));
        motion.Initialize();
        await HomeAsync(motion);
        await station.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        using var firstStop = new CancellationTokenSource();
        var firstRun = process.RunAsync(recipe, firstStop.Token);
        Assert.True(await WaitUntilAsync(
            () => work.Assembly(HeatSinkSlot.HeatSink1)
                .PcbBoltResults.Count == 1,
            TimeSpan.FromSeconds(5)));
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.False(work.Assembly(HeatSinkSlot.HeatSink1)
            .PcbBoltResults[2].Success);

        using var cancellation = new CancellationTokenSource();
        var resumedRun = process.RunAsync(recipe, cancellation.Token);
        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await resumedRun;
        feederCancellation.Cancel();
        await feederRuns;

        Assert.True(completed);
        Assert.Equal(BoltFasteningState.WaitingForTransfer, work.State);
        Assert.Equal(2, work.Assemblies.Count);
        var heatSink1 = work.Assembly(HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assembly(HeatSinkSlot.HeatSink2);
        Assert.Equal(AssemblyResult.Ng, heatSink1.FasteningResult);
        Assert.Equal(AssemblyResult.Ok, heatSink2.FasteningResult);
        Assert.False(heatSink1.PcbBoltResults[2].Success);
        Assert.True(heatSink1.IpmSeatingResults[1].Success);
        Assert.True(heatSink1.IpmFinalResults[1].Success);
        Assert.True(heatSink2.PcbBoltResults[4].Success);
        Assert.True(heatSink2.IpmSeatingResults[3].Success);
        Assert.True(heatSink2.IpmFinalResults[3].Success);
        Assert.Equal(BoltHeadState.Ready, pickupHead.State);
        Assert.Equal(BoltHeadState.Ready, shootingHead.State);
    }

    private static BoltPoint Bolt(
        int number,
        HeatSinkSlot heatSink,
        FasteningHead head,
        double x,
        double y) => new()
        {
            Number = number,
            HeatSink = heatSink,
            Head = head,
            X = x,
            Y = y,
            Z = 10,
        };

    private static BoltHeadSettings HeadSettings() => new()
    {
        UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
        LowerRightLocatingPin = new AxisPosition { X = 100, Y = 0 },
    };

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 20_000);
        await motion.HomeAsync(MotionAxis.X, 20_000);
        await motion.HomeAsync(MotionAxis.Y, 20_000);
    }

}
