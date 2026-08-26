using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFeeder;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Fact]
    public async Task LinearFeederUsesItsOwnTimeout()
    {
        var io = new VirtualIoService(
            new BoltFeederHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 500 });
        var feeder = new LinearBoltFeeder(
            io,
            new BoltFeederSettings
            {
                LinearTimeoutMilliseconds = 50,
            });

        io.Initialize();
        var exception = await Assert.ThrowsAsync<IoTimeoutException>(
            () => feeder.RunAsync());

        Assert.Contains("50 ms", exception.Message);
        Assert.False(feeder.RunCommandOn);
    }

    [Fact]
    public async Task LinearFeederKeepsTheNextBoltReady()
    {
        var io = new VirtualIoService(
            new BoltFeederHardwareSettings().Outputs,
            new MachineOptions());
        _ = new VirtualMachine(io);
        var feeder = new LinearBoltFeeder(
            io,
            new BoltFeederSettings
            {
                LinearTimeoutMilliseconds = 500,
            });
        var refillCount = 0;
        var runCount = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.LinearFeederRunSignal && value)
            {
                Interlocked.Increment(ref runCount);
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.LinearFeederBoltDetected && value)
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
                  && !feeder.RunCommandOn,
            TimeSpan.FromSeconds(1));
        io.SetInput(InputIo.LinearFeederBoltDetected, false);
        await Task.Delay(50);
        Assert.Equal(1, refillCount);
        var nextBolt = await WaitUntilAsync(
            () => runCount == 2
                  && refillCount == 2
                  && feeder.State == BoltFeederState.BoltReady
                  && !feeder.RunCommandOn,
            TimeSpan.FromSeconds(1));

        cancellation.Cancel();
        await run;

        Assert.True(firstBolt);
        Assert.True(nextBolt);
        Assert.False(feeder.RunCommandOn);
    }

    [Fact]
    public async Task BoltProcessCompletesTheCarrierAfterFastening()
    {
        var operations = new OperationCancellation();
        var settings = new BoltFasteningSettings
        {
            Motion = new()
            {
                HorizontalSpeed = 2_000,
                ZSpeed = 2_000,
            },
            SafeZ = 0,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(
                new BoltFasteningHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        _ = new VirtualMachine(io);
        var bus = new VirtualAdcBus();
        var connection = new HantasSettings
        {
            PortName = "Virtual",
        };
        var pickupHead = new AdcBoltHead(bus, connection, 1);
        var shootingHead = new AdcBoltHead(bus, connection, 2);
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
            settings);
        var work = new BoltFasteningWork(io);
        var feederSettings = new BoltFeederSettings();
        var pickupFeeder = new PickupBoltFeeder(io, feederSettings);
        var linearFeeder = new LinearBoltFeeder(io, feederSettings);
        var process = new BoltFasteningProcess(
            station,
            work,
            pickupFeeder,
            linearFeeder);
        var recipe = new BoltFasteningRecipe
        {
            PcbPreset = 4,
            IpmSeatingPreset = 3,
            IpmFinalPreset = 5,
            BoltPoints =
            [
                new()
                {
                    Number = 1,
                    Housing = HousingSlot.Housing1,
                    Head = FasteningHead.Pickup,
                    X = 20,
                    Y = 30,
                    Z = 10,
                },
                new()
                {
                    Number = 2,
                    Housing = HousingSlot.Housing1,
                    Head = FasteningHead.Shooting,
                    X = 20,
                    Y = 30,
                    Z = 10,
                },
            ],
        };

        io.Initialize();
        io.SetInput(InputIo.LinearFeederBoltDetected, true);
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.Z, 2_000);
        await motion.HomeAsync(MotionAxis.X, 2_000);
        await motion.HomeAsync(MotionAxis.Y, 2_000);
        await station.CheckReadyAsync();
        Assert.Equal(
            BoltFasteningState.WaitingForCarrier,
            work.State);
        using var cancellation = new CancellationTokenSource();
        var run = process.RunAsync(recipe, cancellation.Token);
        Assert.True(io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.False(station.PickupBoltLoaded);
        Assert.False(io.GetOutput(OutputIo.BoltHead1VacuumPump));
        Assert.Equal(
            BoltFasteningState.WaitingForCarrier,
            work.State);
        io.SetInput(InputIo.BoltFasteningCarrierJigPresent, true);
        Assert.Equal(
            BoltFasteningState.WaitingForSeat,
            work.State);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningHousing1Present, true);
        Assert.Equal(
            BoltFasteningState.ReadyToFasten,
            work.State);

        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run;

        Assert.True(completed);
        Assert.Equal(
            BoltFasteningState.WaitingForTransfer,
            work.State);
        Assert.Equal((20, 30, 0), motion.GetPosition());
        Assert.True(io.GetInput(InputIo.BoltTableUp));
        Assert.False(io.GetInput(InputIo.BoltTableDown));
        Assert.True(io.GetInput(InputIo.BoltHead1Up));
        Assert.False(io.GetInput(InputIo.BoltHead1Down));
        Assert.False(io.GetInput(InputIo.BoltHead1VacuumDetected));
        Assert.False(io.GetOutput(OutputIo.BoltHead1VacuumPump));
        Assert.False(io.GetInput(InputIo.BoltHead2VacuumDetected));
        Assert.False(io.GetOutput(OutputIo.BoltHead2VacuumPump));
        Assert.False(io.GetInput(InputIo.ShootingTubeBoltDetected));
        Assert.False(io.GetInput(InputIo.ShootingEscapeForward));
        Assert.True(io.GetInput(InputIo.ShootingEscapeBackward));
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HousingSlot.Housing1, assembly.Housing);
        Assert.Equal(PcbResult.Ok, assembly.FasteningResult);
        Assert.Single(assembly.PcbBoltResults);
        Assert.Single(assembly.IpmSeatingResults);
        Assert.Single(assembly.IpmFinalResults);
        Assert.True(assembly.PcbBoltResults[2].Success);
        Assert.True(assembly.IpmSeatingResults[1].Success);
        Assert.True(assembly.IpmFinalResults[1].Success);

        var pickupResult = await bus.ReadFasteningResultAsync(1);
        var shootingResult = await bus.ReadFasteningResultAsync(2);
        Assert.Equal((ushort)5, pickupResult.Preset);
        Assert.Equal((ushort)2, pickupResult.ScrewCount);
        Assert.Equal((ushort)4, shootingResult.Preset);
        Assert.Equal((ushort)1, shootingResult.ScrewCount);
    }

    private static BoltHeadSettings HeadSettings() => new()
    {
        UpperLeftLocatingPin = new AxisPos { X = 0, Y = 0 },
        LowerRightLocatingPin = new AxisPos { X = 100, Y = 0 },
    };

    private static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings) =>
        settings
            .SelectMany(section => section.Outputs)
            .ToDictionary();

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
