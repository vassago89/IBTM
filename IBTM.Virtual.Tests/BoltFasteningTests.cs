using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class BoltFasteningTests
{
    [Fact]
    public void RecoveryPreservesMeasuredResultsUntilExplicitlyUnchecked()
    {
        var io = new VirtualIoService(
            Outputs(new ConveyorHardwareSettings()), new MachineOptions());
        var work = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        io.Initialize();
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        var first = work.Assembly(HeatSinkSlot.HeatSink1);
        var second = work.Assembly(HeatSinkSlot.HeatSink2);
        var pcbNg = new BoltResult(false, 1.25);
        var seatingOk = new BoltResult(true, 0.8);
        var finalNg = new BoltResult(false, 2.3);
        var secondPcbOk = new BoltResult(true, 1.4);
        first.RecordPcbBolt(1, pcbNg);
        first.RecordIpmSeating(2, seatingOk);
        first.RecordIpmFinal(2, finalNg);
        second.RecordPcbBolt(3, secondPcbOk);
        work.Complete();

        (HeatSinkSlot HeatSink, int Number, FasteningPass Pass, bool Completed)[] items =
        [
            (HeatSinkSlot.HeatSink1, 1, FasteningPass.Pcb, true),
            (HeatSinkSlot.HeatSink1, 4, FasteningPass.IpmSeating, true),
        ];
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
        work.PrepareRecovery(items);

        Assert.Same(pcbNg, first.PcbBoltResults[1]);
        Assert.Same(seatingOk, first.IpmSeatingResults[2]);
        Assert.Same(finalNg, first.IpmFinalResults[2]);
        Assert.Same(secondPcbOk, second.PcbBoltResults[3]);
        Assert.Equal(BoltResultSource.Manual, first.IpmSeatingResults[4].Source);
        Assert.Equal(AssemblyResult.Ng, first.FasteningResult);
        Assert.True(work.HasNg);
        Assert.False(work.Completed);

        work.PrepareRecovery(
        [
            (HeatSinkSlot.HeatSink1, 1, FasteningPass.Pcb, false),
        ]);

        Assert.Empty(first.PcbBoltResults);
        Assert.Same(finalNg, first.IpmFinalResults[2]);
        Assert.Equal(AssemblyResult.Ng, first.FasteningResult);
        Assert.True(work.HasNg);

        work.PrepareRecovery(
        [
            (HeatSinkSlot.HeatSink1, 2, FasteningPass.IpmFinal, false),
        ]);

        Assert.Empty(first.IpmFinalResults);
        Assert.Same(seatingOk, first.IpmSeatingResults[2]);
        Assert.Equal(BoltResultSource.Manual, first.IpmSeatingResults[4].Source);
        Assert.Same(secondPcbOk, second.PcbBoltResults[3]);
        Assert.Equal(AssemblyResult.Pending, first.FasteningResult);
        Assert.False(work.HasNg);
        Assert.False(work.Completed);
    }

    [Theory]
    [InlineData(AdcFunctionCode.ReadInputRegisters)]
    [InlineData(AdcFunctionCode.WriteSingleRegister)]
    public async Task FailedFasteningPreparationStillStopsTheHead(
        AdcFunctionCode failingFunction)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.CheckReadyAsync();
        var failed = false;
        var stops = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit) return;
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))
                    == (ushort)AdcRemoteRegister.RemoteStart
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) == 0)
            {
                stops++;
            }
            else if (!failed && frame[1] == (byte)failingFunction)
            {
                failed = true;
                throw new IOException("Injected ADC communication failure.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => head.TightenAsync());
        Assert.Equal(1, stops);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.True((await head.TightenAsync()).Success);
        Assert.Equal(2, stops);
        Assert.Equal(BoltHeadState.Ready, head.State);
    }

    [Fact]
    public async Task ControllerErrorStopsUntilReset()
    {
        IAdcBus bus = new VirtualAdcBus();
        var virtualBus = (VirtualAdcBus)bus;
        var head = new AdcBoltHead(bus, new HantasSettings(), 2);
        await head.CheckReadyAsync();
        virtualBus.SetNextFasteningResult(2, AdcEventStatus.Error);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => head.TightenAsync());

        Assert.Equal(BoltHeadState.Ready, head.State);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(2)).EventCount);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => head.CheckReadyAsync());

        virtualBus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        await head.SelectPresetAsync(3);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.SetDirectionAsync(2, AdcDirection.Loosening);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StartAsync(2);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StopAsync(2);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => head.CheckReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => head.TightenAsync());
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(2)).EventCount);

        await bus.ResetAlarmAsync(2);
        await head.CheckReadyAsync();
        Assert.False((await head.TightenAsync()).Success);
        Assert.Equal(2, (await bus.ReadFasteningResultAsync(2)).EventCount);
    }

    [Fact]
    public async Task ResultQueuedDuringFasteningAppliesOnceToSelectedSlave()
    {
        var bus = new VirtualAdcBus();
        var selected = new AdcBoltHead(bus, new HantasSettings(), 1);
        var other = new AdcBoltHead(bus, new HantasSettings(), 2);
        var current = selected.TightenAsync();
        Assert.False(current.IsCompleted);

        bus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);

        Assert.True((await current).Success);
        Assert.True((await other.TightenAsync()).Success);
        Assert.False((await selected.TightenAsync()).Success);
        Assert.True((await selected.TightenAsync()).Success);
    }

    [Fact]
    public async Task CancelledFasteningDoesNotCompleteAndPreservesTheNextResult()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.SelectPresetAsync(3);
        using var stop = new CancellationTokenSource();
        var tightening = head.TightenAsync(stop.Token);

        Assert.Equal(BoltHeadState.Tightening, head.State);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        ((VirtualAdcBus)bus).SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tightening);

        await Task.Delay(300);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.Equal(BoltHeadState.Tightening, head.State);

        Assert.False((await head.TightenAsync()).Success);
        var completed = await bus.ReadFasteningResultAsync(1);
        Assert.Equal(1, completed.EventCount);
        Assert.Equal(3, completed.Preset);
        Assert.Equal(BoltHeadState.Ready, head.State);
        Assert.True((await head.TightenAsync()).Success);
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FasteningPreservesPassOrderAndCarrierResults(
        bool shortShootingPulse)
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
        if (shortShootingPulse)
        {
            io.OutputChanged += (output, value) =>
            {
                if (output == OutputIo.ShootBolt && value)
                {
                    io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                    io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                    io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                }
            };
        }
        var bus = new VirtualAdcBus();
        var presets = new Dictionary<byte, ushort>();
        var tightenings = new List<(byte Head, ushort Preset)>();
        Action? afterStart = null;
        Action? afterStop = null;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit
                || frame[1] != (byte)AdcFunctionCode.WriteSingleRegister)
            {
                return;
            }

            var register = (AdcRemoteRegister)BinaryPrimitives
                .ReadUInt16BigEndian(frame.AsSpan(2));
            var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
            if (register == AdcRemoteRegister.Preset)
            {
                presets[frame[0]] = value;
            }
            else if (register == AdcRemoteRegister.RemoteStart)
            {
                if (value != 0)
                {
                    tightenings.Add((frame[0], presets[frame[0]]));
                    afterStart?.Invoke();
                }
                else
                {
                    afterStop?.Invoke();
                }
            }
        };
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
        var gantry = new BoltFasteningGantry(
            shootingHead,
            pickupHead,
            io,
            motion,
            settings,
            carrierReference);
        var movedWithLoweredCylinder = false;
        motion.PositionChanged += (_, _, _) =>
            movedWithLoweredCylinder |= motion.IsMovingHorizontal && !gantry.CanMoveHorizontal;
        var work = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var feederSettings = new BoltFeederSettings();
        var pickupFeeder = new PickupBoltFeeder(io, feederSettings);
        var shootingFeeder = new ShootingBoltFeeder(io, feederSettings);
        var station = new BoltFasteningStation(
            gantry,
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
        await HomeAsync(motion, 20_000);
        await gantry.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        void LoseHeatSinkInputs()
        {
            afterStart = null;
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, false);
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
        }
        afterStart = LoseHeatSinkInputs;

        using var firstStop = new CancellationTokenSource();
        var firstRun = station.RunAsync(recipe, firstStop.Token);
        var firstBoltCompleted = await WaitUntilAsync(
            () => work.Assembly(HeatSinkSlot.HeatSink1)
                .PcbBoltResults.Count == 1,
            TimeSpan.FromSeconds(5));
        Assert.True(
            firstBoltCompleted,
            $"State={station.State(recipe)}, Run={firstRun.Status}, "
            + $"Pickup={gantry.PickupHeadPosition}, "
            + $"Shooting={gantry.ShootingHeadPosition}, "
            + $"Loaded={gantry.ShootingBoltLoaded}, "
            + $"Error={firstRun.Exception?.GetBaseException().Message}");
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.False(work.Assembly(HeatSinkSlot.HeatSink1)
            .PcbBoltResults[2].Success);
        Assert.False(io.GetInput(InputIo.BoltFasteningHeatSink1Present));
        Assert.False(io.GetInput(InputIo.BoltFasteningHeatSink2Present));

        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        afterStart = LoseHeatSinkInputs;
        using var cancellation = new CancellationTokenSource();
        var resumedRun = station.RunAsync(recipe, cancellation.Token);
        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await resumedRun;
        feederCancellation.Cancel();
        await feederRuns;

        Assert.True(
            completed,
            $"State={station.State(recipe)}, Run={resumedRun.Status}, "
            + $"Pickup={gantry.PickupHeadPosition}, "
            + $"Shooting={gantry.ShootingHeadPosition}, "
            + $"PickupLoaded={gantry.PickupBoltLoaded}, "
            + $"ShootingLoaded={gantry.ShootingBoltLoaded}, "
            + $"Error={resumedRun.Exception?.GetBaseException().Message}");
        Assert.Equal(2, work.Assemblies.Count());
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
        Assert.False(movedWithLoweredCylinder);
        Assert.True(gantry.CanMoveHorizontal);
        Assert.Equal(
            new (byte Head, ushort Preset)[]
            {
                (2, 4), (2, 4), (1, 3), (1, 3), (1, 5), (1, 5),
            },
            tightenings);

        io.SetInput(InputIo.BoltFasteningCarrierPresent, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        var previousAssembly = work.Assembly(HeatSinkSlot.HeatSink1);
        using var carrierChange = new CancellationTokenSource();
        afterStop = () =>
        {
            io.SetInput(InputIo.BoltFasteningCarrierPresent, false);
            io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
            carrierChange.Cancel();
        };

        await station.RunAsync(recipe, carrierChange.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(previousAssembly.PcbBoltResults);
        Assert.Empty(work.Assemblies);
        Assert.False(work.Completed);
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

}
