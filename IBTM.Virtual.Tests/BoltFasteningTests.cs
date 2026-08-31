using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
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

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Fact]
    public async Task RecoveryDiscardsTheInterruptedAdcResult()
    {
        var bus = new VirtualAdcBus();
        var head = new AdcBoltHead(
            bus,
            new HantasSettings { PortName = "Virtual" },
            1);
        using var cancellation = new CancellationTokenSource();
        var starts = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit
                || frame[1] != 0x06
                || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))
                    != (ushort)AdcRemoteRegister.RemoteStart
                || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4))
                    != 1)
            {
                return;
            }

            if (Interlocked.Increment(ref starts) == 1)
            {
                cancellation.Cancel();
            }
        };

        await head.CheckReadyAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => head.TightenAsync(cancellation.Token));

        Assert.Equal(BoltHeadState.Tightening, head.State);

        head.DiscardPendingResult();
        var result = await head.TightenAsync();

        Assert.Equal(BoltHeadState.Ready, head.State);
        Assert.Equal(2, starts);
        Assert.True(result.Success);
    }

    [Fact]
    public void RecoveryClearsMeasuredResultsAndAppliesManualWork()
    {
        var io = new VirtualIoService(
            new Dictionary<OutputIo, OutputHardware>(),
            new MachineOptions());
        var work = new BoltFasteningWork(io);
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordPcbBolt(1, new BoltResult(false, 2.4));
        assembly.RecordIpmFinal(2, new BoltResult(true, 3.1));
        work.Complete();

        work.PrepareRecovery([
            (HeatSinkSlot.HeatSink1, 3, FasteningPass.IpmSeating),
        ]);

        Assert.False(work.Completed);
        Assert.Empty(assembly.PcbBoltResults);
        Assert.Empty(assembly.IpmFinalResults);
        var manual = Assert.Single(assembly.IpmSeatingResults).Value;
        Assert.True(manual.Success);
        Assert.Equal(BoltResultSource.Manual, manual.Source);
    }

    [Fact]
    public void NgStateOnlyUsesDetectedHeatSinks()
    {
        var io = new VirtualIoService(
            new Dictionary<OutputIo, OutputHardware>(),
            new MachineOptions());
        var work = new BoltFasteningWork(io);
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        work.Assembly(HeatSinkSlot.HeatSink1)
            .RecordPcbBolt(1, new BoltResult(false, 2.4));

        Assert.True(work.HasNg);

        io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);

        Assert.False(work.HasNg);
    }

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
        _ = new VirtualMachine(io, []);
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
                HorizontalSpeed = 20_000,
                ZSpeed = 100,
            },
            SafeZ = 0,
            PickupPosition = new AxisPos { X = 10, Y = 10, Z = 10 },
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
        var connection = new HantasSettings
        {
            PortName = "Virtual",
        };
        var pickupHead = new AdcBoltHead(bus, connection, 1);
        var shootingHead = new AdcBoltHead(bus, connection, 2);
        var carrierReference = new CarrierReferenceSettings
        {
            UpperLeftPin = new AxisPos { X = 0, Y = 0 },
            LowerRightPin = new AxisPos { X = 100, Y = 0 },
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
                    HeatSink = HeatSinkSlot.HeatSink1,
                    Head = FasteningHead.Pickup,
                    X = 20,
                    Y = 30,
                    Z = 10,
                },
                new()
                {
                    Number = 2,
                    HeatSink = HeatSinkSlot.HeatSink1,
                    Head = FasteningHead.Shooting,
                    X = 20,
                    Y = 30,
                    Z = 10,
                },
                new()
                {
                    Number = 3,
                    HeatSink = HeatSinkSlot.HeatSink2,
                    Head = FasteningHead.Pickup,
                    X = 30,
                    Y = 40,
                    Z = 10,
                },
                new()
                {
                    Number = 4,
                    HeatSink = HeatSinkSlot.HeatSink2,
                    Head = FasteningHead.Shooting,
                    X = 30,
                    Y = 40,
                    Z = 10,
                },
            ],
        };

        io.Initialize();
        io.SetInput(InputIo.LinearFeederBoltDetected, true);
        using var feederCancellation = new CancellationTokenSource();
        var feederRuns = Task.WhenAll(
            pickupFeeder.RunAsync(feederCancellation.Token),
            linearFeeder.RunAsync(feederCancellation.Token));
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.Z, 20_000);
        await motion.HomeAsync(MotionAxis.X, 20_000);
        await motion.HomeAsync(MotionAxis.Y, 20_000);
        await station.CheckReadyAsync();
        Assert.Equal(
            BoltFasteningState.WaitingForCarrier,
            work.State);
        using var firstStop = new CancellationTokenSource();
        using var raisingStop = new CancellationTokenSource();
        using var pickupStop = new CancellationTokenSource();
        using var finalStop = new CancellationTokenSource();
        var selectedPresets = new ushort[3];
        var starts = new List<(byte Head, ushort Preset)>();
        var pickupStarts = 0;
        var presetChanges = 0;
        var pickupLoads = 0;
        var shootingLoads = 0;
        var movingPickupSafe = true;
        var shootingStartSafe = true;
        var pickupHeadDownAtStart = true;
        var shootingHeadUpAtPickupStart = true;
        var pickupBoltLoadedAtStart = true;
        var pickupMotionStoppedAtStart = true;
        var stoppedWhileRaising = 0;
        motion.PositionChanged += (x, y, z) =>
        {
            if (station.PickupBoltLoaded
                && Math.Abs(x - settings.PickupPosition.X) < 0.05
                && Math.Abs(y - settings.PickupPosition.Y) < 0.05
                && z > settings.SafeZ + 0.05
                && z < settings.PickupPosition.Z - 0.05
                && Interlocked.Exchange(ref stoppedWhileRaising, 1) == 0)
            {
                raisingStop.Cancel();
            }
        };
        motion.MovingChanged += moving =>
        {
            if (moving)
            {
                movingPickupSafe &=
                    station.PickupHead == BoltCylinderState.Up;
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (!value)
            {
                return;
            }

            if (input == InputIo.BoltHead1VacuumDetected)
            {
                Interlocked.Increment(ref pickupLoads);
            }
            else if (input == InputIo.BoltHead2VacuumDetected)
            {
                Interlocked.Increment(ref shootingLoads);
            }
        };
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit
                || frame[1] != 0x06)
            {
                return;
            }

            var head = frame[0];
            var register = (AdcRemoteRegister)
                BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            var value = BinaryPrimitives.ReadUInt16BigEndian(
                frame.AsSpan(4));
            if (register == AdcRemoteRegister.Preset)
            {
                selectedPresets[head] = value;
                Interlocked.Increment(ref presetChanges);
                return;
            }

            if (register != AdcRemoteRegister.RemoteStart || value != 1)
            {
                return;
            }

            starts.Add((head, selectedPresets[head]));
            if (head == 2)
            {
                shootingStartSafe &=
                    station.ShootingHead == BoltCylinderState.Down
                    && station.PickupHead == BoltCylinderState.Up
                    && station.ShootingBoltLoaded
                    && !motion.IsMoving;
                firstStop.Cancel();
                return;
            }

            pickupHeadDownAtStart &=
                station.PickupHead == BoltCylinderState.Down;
            shootingHeadUpAtPickupStart &=
                station.ShootingHead == BoltCylinderState.Up;
            pickupBoltLoadedAtStart &=
                selectedPresets[head] == recipe.IpmFinalPreset
                || station.PickupBoltLoaded;
            pickupMotionStoppedAtStart &= !motion.IsMoving;
            var start = Interlocked.Increment(ref pickupStarts);
            if (start == 1)
            {
                pickupStop.Cancel();
            }
            else if (start == 3)
            {
                finalStop.Cancel();
            }
        };
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        firstStop.CancelAfter(TimeSpan.FromSeconds(5));
        var firstRun = process.RunAsync(recipe, firstStop.Token);
        Assert.True(io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.False(station.PickupBoltLoaded);
        Assert.False(io.GetOutput(OutputIo.BoltHead1VacuumPump));
        Assert.Equal(
            BoltFasteningState.WaitingForCarrier,
            work.State);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        Assert.Equal(
            BoltFasteningState.WaitingForSeat,
            work.State);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        Assert.Equal(
            BoltFasteningState.ReadyToFasten,
            work.State);
        Assert.True(io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.True(io.GetInput(InputIo.LinearFeederBoltDetected));

        await firstRun;

        Assert.False(work.Completed);
        Assert.Equal(BoltHeadState.Tightening, shootingHead.State);
        raisingStop.CancelAfter(TimeSpan.FromSeconds(10));
        var raisingRun = process.RunAsync(recipe, raisingStop.Token);

        await raisingRun;

        Assert.Equal(1, stoppedWhileRaising);
        Assert.True(station.PickupBoltLoaded);
        Assert.Equal(
            BoltFasteningProcessState.RaisingPickedBolt,
            process.State(recipe));
        pickupStop.CancelAfter(TimeSpan.FromSeconds(5));
        var pickupRun = process.RunAsync(recipe, pickupStop.Token);

        await pickupRun;

        Assert.False(work.Completed);
        Assert.Equal(BoltHeadState.Tightening, pickupHead.State);
        finalStop.CancelAfter(TimeSpan.FromSeconds(5));
        var finalStopRun = process.RunAsync(recipe, finalStop.Token);

        await finalStopRun;

        Assert.False(work.Completed);
        Assert.Equal(BoltHeadState.Tightening, pickupHead.State);
        using var cancellation = new CancellationTokenSource();
        var run = process.RunAsync(recipe, cancellation.Token);

        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await run;
        feederCancellation.Cancel();
        await feederRuns;

        Assert.True(completed);
        Assert.Equal(
            BoltFasteningState.WaitingForTransfer,
            work.State);
        Assert.Equal((30, 40, 0), motion.GetPosition());
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
        Assert.Equal(
            [
                (Head: (byte)2, Preset: (ushort)4),
                (Head: (byte)2, Preset: (ushort)4),
                (Head: (byte)1, Preset: (ushort)3),
                (Head: (byte)1, Preset: (ushort)3),
                (Head: (byte)1, Preset: (ushort)5),
                (Head: (byte)1, Preset: (ushort)5),
            ],
            starts);
        Assert.Equal(3, presetChanges);
        Assert.Equal(2, pickupLoads);
        Assert.Equal(2, shootingLoads);
        Assert.True(movingPickupSafe);
        Assert.True(shootingStartSafe);
        Assert.True(pickupHeadDownAtStart);
        Assert.True(shootingHeadUpAtPickupStart);
        Assert.True(pickupBoltLoadedAtStart);
        Assert.True(pickupMotionStoppedAtStart);
        Assert.Equal(2, work.Assemblies.Count);
        var heatSink1 = work.Assembly(HeatSinkSlot.HeatSink1);
        var heatSink2 = work.Assembly(HeatSinkSlot.HeatSink2);
        Assert.Equal(PcbResult.Ng, heatSink1.FasteningResult);
        Assert.Equal(PcbResult.Ok, heatSink2.FasteningResult);
        Assert.False(heatSink1.PcbBoltResults[2].Success);
        Assert.True(heatSink1.IpmSeatingResults[1].Success);
        Assert.True(heatSink1.IpmFinalResults[1].Success);
        Assert.True(heatSink2.PcbBoltResults[4].Success);
        Assert.True(heatSink2.IpmSeatingResults[3].Success);
        Assert.True(heatSink2.IpmFinalResults[3].Success);

        var pickupResult = await bus.ReadFasteningResultAsync(1);
        var shootingResult = await bus.ReadFasteningResultAsync(2);
        Assert.Equal((ushort)5, pickupResult.Preset);
        Assert.Equal((ushort)4, pickupResult.ScrewCount);
        Assert.Equal((ushort)4, shootingResult.Preset);
        Assert.Equal((ushort)2, shootingResult.ScrewCount);
        Assert.Equal(BoltHeadState.Ready, pickupHead.State);
        Assert.Equal(BoltHeadState.Ready, shootingHead.State);
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
