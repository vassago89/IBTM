using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualReverseStopsOnReleaseOrCommunicationFailureWithoutACompletionResult(bool communicationFailure)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 2);
        var started = false;
        var stops = 0;
        var writes = new List<(byte Slave, ushort Address, ushort Value)>();
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit) return;
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                writes.Add((frame[0], address, value));
                if (address == (ushort)AdcRemoteRegister.RemoteStart)
                {
                    if (value == 0) stops++;
                    else started = true;
                }
            }
            if (communicationFailure && started && stops == 0
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters)
                throw new IOException("Reverse monitoring failed.");
        };
        using var release = new CancellationTokenSource();
        var running = head.RunReverseAsync(release.Token);
        Assert.True(started);
        if (communicationFailure)
            await Assert.ThrowsAsync<IOException>(() => running);
        else
        {
            await Task.Delay(300);
            Assert.False(running.IsCompleted);
            var status = await bus.ReadControllerStatusAsync(2);
            Assert.True(status.Running);
            Assert.Equal(AdcDirection.Loosening, status.Direction);
            release.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        Assert.Equal(new[]
        {
            ((byte)2, (ushort)AdcRemoteRegister.Direction, (ushort)AdcDirection.Loosening),
            ((byte)2, (ushort)AdcRemoteRegister.RemoteStart, (ushort)1),
            ((byte)2, (ushort)AdcRemoteRegister.RemoteStart, (ushort)0),
        }, writes);
        Assert.Equal(1, stops);
        Assert.False((await bus.ReadControllerStatusAsync(2)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(2)).EventCount);
        Assert.True((await head.TightenAsync()).Success); // Forward explicitly restores its own direction.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerialCancellationAbortsAndDrainsTheNativeOperation(bool timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var nativeIo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action abort = () => abortCalled.SetResult();
        var wait = (Task)typeof(AdcBus).GetMethod("AwaitSerialIoAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [nativeIo.Task, abort, cancellation.Token])!;
        if (timeout) cancellation.CancelAfter(20);
        else cancellation.Cancel();
        await abortCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wait.IsCompleted); // A following bus request must not overlap the aborted native IO.
        nativeIo.SetException(new IOException("Native serial IO aborted."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("crc")]
    [InlineData("address")]
    [InlineData("controller-error")]
    public async Task AdcRawReceiveLogsBytesBeforeResponseValidation(string responseKind)
    {
        var frame = AdcRtuFrame.Build(
            (byte)(responseKind == "address" ? 1 : 0),
            (AdcFunctionCode)(responseKind == "controller-error" ? 0x84 : 0x04),
            responseKind == "controller-error" ? [0x02] : [0x02, 0x12, 0x34]);
        if (responseKind == "crc") frame[^1] ^= 0xFF;
        using var stream = new AdcResponseStream(frame);
        var chunks = new List<byte[]>();
        var reading = ReadAdcResponseAsync(stream, chunks.Add, CancellationToken.None);

        if (responseKind == "valid")
            Assert.Equal(frame, await reading.WaitAsync(TimeSpan.FromSeconds(2)));
        else if (responseKind == "controller-error")
            Assert.Contains("IllegalAddress", (await Assert.ThrowsAsync<IOException>(() => reading)).Message);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => reading);

        Assert.Equal(frame, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Theory]
    [InlineData(0)] // No response.
    [InlineData(1)] // Partial header.
    [InlineData(4)] // Header and partial payload.
    public async Task AdcRawReceiveKeepsPartialBytesWhenTheReadIsAborted(int receivedCount)
    {
        byte[] partial = [0x00, 0x04, 0x02, 0x12];
        using var stream = new AdcResponseStream(partial[..receivedCount]);
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<byte[]>();
        var reading = ReadAdcResponseAsync(stream, chunks.Add, cancellation.Token);
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Already visible before the incomplete response times out or is canceled.
        Assert.Equal(partial[..receivedCount], chunks.SelectMany(chunk => chunk).ToArray());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.Aborted);
        Assert.Equal(partial[..receivedCount], chunks.SelectMany(chunk => chunk).ToArray());
    }

    private static Task<byte[]> ReadAdcResponseAsync(
        AdcResponseStream stream, Action<byte[]> received, CancellationToken cancellationToken) =>
        (Task<byte[]>)typeof(AdcBus).GetMethod("ReadResponseAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [stream, (Action)stream.Abort, received, (byte)0,
                AdcFunctionCode.ReadInputRegisters, cancellationToken])!;

    private sealed class AdcResponseStream(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Aborted { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return base.ReadAsync(buffer[..1], cancellationToken);
            Waiting.TrySetResult();
            return new(_pending.Task); // Model Windows native IO ignoring cancellation.
        }

        public void Abort()
        {
            Aborted = true;
            _pending.TrySetException(new IOException("Native serial IO aborted."));
        }
    }

    [Fact]
    public async Task AdcReadinessUsesLiveRunAndDoesNotCallReverseAFastening()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.CheckReadyAsync();
        Assert.Equal((ushort)1, (await bus.ReadControllerStatusAsync(1)).Preset);
        var statusReads = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcStatusRegister.Preset)
                statusReads++;
        };
        await head.SelectPresetAsync(3);
        Assert.Equal(1, statusReads);
        Assert.Equal((ushort)3, (await bus.ReadControllerStatusAsync(1)).Preset);

        await bus.SetDirectionAsync(1, AdcDirection.Loosening);
        await bus.StartAsync(1);
        var running = await bus.ReadControllerStatusAsync(1);
        Assert.True(running.Running);
        Assert.False(running.Ready);
        Assert.Equal(AdcDirection.Loosening, running.Direction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
        await Task.Delay(300);
        Assert.True((await bus.ReadControllerStatusAsync(1)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        await bus.StopAsync(1);
        await head.CheckReadyAsync();
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);
    }

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
        Assert.True((await head.TightenAsync()).Success);
        using var stop = new CancellationTokenSource();
        var tightening = head.TightenAsync(stop.Token);

        Assert.Equal(BoltHeadState.Tightening, head.State);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        ((VirtualAdcBus)bus).SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tightening);

        await Task.Delay(300);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.Equal(BoltHeadState.Tightening, head.State);

        Assert.False((await head.TightenAsync()).Success);
        var completed = await bus.ReadFasteningResultAsync(1);
        Assert.Equal(2, completed.EventCount);
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
            SafeZ = 5,
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
        var observeFastening = false;
        var leftSafeZAwayFromPickup = false;
        motion.PositionChanged += (x, y, z) =>
        {
            movedWithLoweredCylinder |= motion.IsMovingHorizontal && !gantry.CanMoveHorizontal;
            if (observeFastening)
                leftSafeZAwayFromPickup |= Math.Abs(z - settings.SafeZ) > MotionService.PositionToleranceMillimeters
                    && (Math.Abs(x - settings.PickupPosition.X) > MotionService.PositionToleranceMillimeters
                        || Math.Abs(y - settings.PickupPosition.Y) > MotionService.PositionToleranceMillimeters);
        };
        var shotWithLoweredHead = false;
        var loweredBeforeBoltReady = false;
        var retractedBeforeFirstShot = false;
        var shots = 0;
        var pickups = 0;
        var pickupSequenceInvalid = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingEscapeForward && !value && shots == 0)
                retractedBeforeFirstShot = true;
            if (output == OutputIo.ShootBolt && value)
            {
                shots++;
                shotWithLoweredHead |= gantry.ShootingHeadPosition != BoltCylinderState.Up;
            }
            if (output == OutputIo.ShootingHeadDown && value)
                loweredBeforeBoltReady |= !gantry.ShootingBoltLoaded
                    || io.GetInput(InputIo.ShootingTubeBoltDetected)
                    || !io.GetInput(InputIo.ShootingEscapeBackward);
            var position = motion.GetPosition();
            var atPickupXy = Math.Abs(position.X - settings.PickupPosition.X) <= MotionService.PositionToleranceMillimeters
                && Math.Abs(position.Y - settings.PickupPosition.Y) <= MotionService.PositionToleranceMillimeters;
            if (output == OutputIo.PickupHeadDown && value && atPickupXy)
                pickupSequenceInvalid |= Math.Abs(position.Z - settings.SafeZ) > MotionService.PositionToleranceMillimeters;
            if (output == OutputIo.PickupHeadDown && !value && atPickupXy && gantry.PickupBoltLoaded)
                pickupSequenceInvalid |= Math.Abs(position.Z - settings.SafeZ) > MotionService.PositionToleranceMillimeters
                    || !io.GetOutput(OutputIo.PickupHeadVacuumPump);
            if (output == OutputIo.PickupHeadVacuumPump && value)
            {
                pickups++;
                pickupSequenceInvalid |= !atPickupXy
                    || Math.Abs(position.Z - settings.PickupPosition.Z) > MotionService.PositionToleranceMillimeters
                    || gantry.PickupHeadPosition != BoltCylinderState.Down;
            }
        };
        var work = new BoltFasteningWork(
            ConveyorStation.BoltFastening(io));
        var feederSettings = new BoltFeederSettings();
        var pickupFeeder = new PickupBoltFeeder(io, feederSettings);
        var shootingFeeder = new ShootingBoltFeeder(io, feederSettings);
        var layout = new PcbLayout
        {
            Width = 50, Height = 50,
            Origins = new()
            {
                [HeatSinkSlot.HeatSink1] = new(),
                [HeatSinkSlot.HeatSink2] = new() { X = 10, Y = 10 },
            },
            BoltPoints =
            [
                Bolt(1, FasteningHead.Pickup, 20, 30),
                Bolt(2, FasteningHead.Shooting, 20, 30),
            ],
        };
        var station = new BoltFasteningStation(
            gantry,
            work,
            pickupFeeder,
            shootingFeeder, () => layout);
        var recipe = new BoltFasteningRecipe
        {
            PcbPreset = 4,
            IpmSeatingPreset = 3,
            IpmFinalPreset = 5,
        };

        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        using var feederCancellation = new CancellationTokenSource();
        var feederRuns = Task.WhenAll(
            pickupFeeder.RunAsync(feederCancellation.Token),
            shootingFeeder.RunAsync(feederCancellation.Token));
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToSafeZAsync();
        observeFastening = true;
        await gantry.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetOutput(OutputIo.BoltFasteningBackupPlateUp, true);
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

        using (var stopDuringAdvance = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            void StopBeforeEscapeFeedback(OutputIo output, bool value)
            {
                if (output != OutputIo.ShootingEscapeForward || !value) return;
                io.SetInput(InputIo.ShootingEscapeBackward, false);
                stopDuringAdvance.Cancel();
            }
            io.OutputChanged += StopBeforeEscapeFeedback;
            await station.RunAsync(recipe, stopDuringAdvance.Token);
            io.OutputChanged -= StopBeforeEscapeFeedback;
            Assert.True(io.GetOutput(OutputIo.ShootingEscapeForward));
            Assert.Equal(0, shots);
            Assert.True(await WaitUntilAsync(
                () => io.GetInput(InputIo.ShootingEscapeForward),
                TimeSpan.FromSeconds(1)));
            Assert.False(gantry.ShootingBoltLoaded);
            Assert.Equal(0, shots);
            io.SetInput(InputIo.ShootingFeederBoltDetected, false);
            Assert.Equal(BoltFasteningState.ShootingBolt, station.State());

            var stoppedPosition = motion.GetPosition();
            await gantry.MoveToXYAsync(stoppedPosition.X + 1, stoppedPosition.Y);
            io.SetInput(InputIo.ShootingTubeBoltDetected, true);
            Assert.Equal(BoltFasteningState.WaitingForShootingTubeClear, station.State());
            io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            Assert.Equal(BoltFasteningState.MovingToPcbBolt, station.State());
        }

        using (var stopAtArrival = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            void StopAtBoltArrival(InputIo input, bool value)
            {
                if (input == InputIo.ShootingHeadVacuumDetected && value)
                    stopAtArrival.Cancel();
            }
            io.InputChanged += StopAtBoltArrival;
            await station.RunAsync(recipe, stopAtArrival.Token);
            io.InputChanged -= StopAtBoltArrival;
            Assert.True(gantry.ShootingBoltLoaded);
            Assert.Equal(BoltCylinderState.Up, gantry.ShootingHeadPosition);
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            Assert.Empty(tightenings);

            io.SetInput(InputIo.ShootingHeadUp, false);
            io.SetInput(InputIo.ShootingHeadDown, true);
            io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
            Assert.Equal(BoltFasteningState.ClearingShootingHead, station.State());
            io.SetInput(InputIo.ShootingHeadDown, false);
            io.SetInput(InputIo.ShootingHeadUp, true);
            io.SetInput(InputIo.ShootingTubeBoltDetected, true);
            Assert.Equal(BoltFasteningState.WaitingForShootingTubeClear, station.State());
            io.SetInput(InputIo.ShootingTubeBoltDetected, false);
            io.SetInput(InputIo.ShootingFeederBoltDetected, false);
            Assert.Equal(BoltFasteningState.ShootingBolt, station.State());
            io.SetInput(InputIo.ShootingEscapeForward, false);
            io.SetInput(InputIo.ShootingEscapeBackward, true);
            Assert.Equal(BoltFasteningState.WaitingForShootingFeeder, station.State());
            io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            Assert.Equal(BoltFasteningState.AdvancingShootingEscape, station.State());
            io.SetInput(InputIo.ShootingEscapeBackward, false);
            io.SetInput(InputIo.ShootingFeederBoltDetected, false);
            Assert.Equal(BoltFasteningState.AdvancingShootingEscape, station.State());
            io.SetInput(InputIo.ShootingEscapeForward, true);
            io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        }

        foreach (var (outputToStop, requestedValue) in new[]
        {
            (OutputIo.ShootingEscapeForward, false),
            (OutputIo.ShootingHeadDown, true),
        })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var interrupted = false;
            void StopBeforeFeedback(OutputIo output, bool value)
            {
                if (output != outputToStop || value != requestedValue) return;
                interrupted = true;
                if (output == OutputIo.ShootingHeadDown)
                    io.SetInput(InputIo.ShootingHeadUp, false);
                stop.Cancel();
            }
            io.OutputChanged += StopBeforeFeedback;
            await station.RunAsync(recipe, stop.Token);
            io.OutputChanged -= StopBeforeFeedback;
            Assert.True(interrupted);
            Assert.True(gantry.ShootingBoltLoaded);
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            Assert.Empty(tightenings);
            Assert.Equal(1, shots);
        }

        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stoppedBeforeResult = false;
        var stopSent = false;
        var readBeforeStop = false;
        var resultReadsAfterCancellation = 0;
        void StopBeforeCompletedResult(AdcFrameDirection direction, byte[] frame)
        {
            if (frame[0] != 2) return;
            if (stoppedBeforeResult)
            {
                if (direction != AdcFrameDirection.Transmit) return;
                if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                    && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))
                        == (ushort)AdcRemoteRegister.RemoteStart
                    && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) == 0)
                    stopSent = true;
                if (frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
                    && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))
                        == (ushort)AdcResultRegister.EventCount)
                {
                    readBeforeStop |= !stopSent;
                    resultReadsAfterCancellation++;
                }
                return;
            }
            if (direction != AdcFrameDirection.Receive
                || frame[1] != (byte)AdcFunctionCode.ReadInputRegisters
                || frame[2] != AdcFasteningResult.RegisterCount * sizeof(ushort)
                || BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3)) == 0)
                return;

            stoppedBeforeResult = true;
            firstStop.Cancel();
            throw new OperationCanceledException(firstStop.Token);
        }
        bus.FrameTransferred += StopBeforeCompletedResult;
        var firstRun = station.RunAsync(recipe, firstStop.Token);
        await firstRun;
        bus.FrameTransferred -= StopBeforeCompletedResult;

        Assert.True(stoppedBeforeResult);
        Assert.True(stopSent);
        Assert.False(readBeforeStop);
        Assert.Equal(1, resultReadsAfterCancellation);
        Assert.Single(tightenings);
        Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1).PcbBoltResults);
        Assert.False(work.Completed);
        Assert.False(work.Assembly(HeatSinkSlot.HeatSink1)
            .PcbBoltResults[2].Success);
        Assert.False(io.GetInput(InputIo.BoltFasteningHeatSink1Present));
        Assert.False(io.GetInput(InputIo.BoltFasteningHeatSink2Present));

        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        using (var stopAfterPickup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            void StopWithPickedBolt(InputIo input, bool value)
            {
                if (input == InputIo.PickupHeadVacuumDetected && value)
                    stopAfterPickup.Cancel();
            }
            io.InputChanged += StopWithPickedBolt;
            await station.RunAsync(recipe, stopAfterPickup.Token);
            io.InputChanged -= StopWithPickedBolt;
            Assert.True(gantry.PickupBoltLoaded);
            Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
            Assert.Equal(settings.PickupPosition.Z, motion.GetPosition().Z);
            Assert.Equal(1, pickups);
            Assert.DoesNotContain(tightenings, item => item.Head == 1);
        }

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
            $"State={station.State()}, Run={resumedRun.Status}, "
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
        Assert.True(heatSink2.PcbBoltResults[2].Success);
        Assert.True(heatSink2.IpmSeatingResults[1].Success);
        Assert.True(heatSink2.IpmFinalResults[1].Success);
        Assert.Equal(BoltHeadState.Ready, pickupHead.State);
        Assert.Equal(BoltHeadState.Ready, shootingHead.State);
        Assert.False(movedWithLoweredCylinder);
        Assert.False(leftSafeZAwayFromPickup);
        Assert.False(shotWithLoweredHead);
        Assert.False(loweredBeforeBoltReady);
        Assert.False(retractedBeforeFirstShot);
        Assert.Equal(2, shots);
        Assert.Equal(2, pickups);
        Assert.False(pickupSequenceInvalid);
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

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task FeederWaitRechecksLateHeadFeedback(FasteningHead head)
    {
        var settings = new BoltFasteningSettings
        {
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(), new ConveyorHardwareSettings()),
            new MachineOptions()) { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(
            settings.Motion, xRange: (0, 100), yRange: (0, 100), zRange: (0, 100),
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new OperationCancellation());
        var bus = new VirtualAdcBus();
        var gantry = new BoltFasteningGantry(
            new AdcBoltHead(bus, new HantasSettings(), 2),
            new AdcBoltHead(bus, new HantasSettings(), 1),
            io, motion, settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100 },
            });
        var layout = new PcbLayout
        {
            Width = 50, Height = 50,
            Origins = new() { [HeatSinkSlot.HeatSink1] = new() },
            BoltPoints = [Bolt(1, head, 10, 10)],
        };
        var feederSettings = new BoltFeederSettings();
        var station = new BoltFasteningStation(
            gantry, new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            new PickupBoltFeeder(io, feederSettings),
            new ShootingBoltFeeder(io, feederSettings), () => layout);
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToXYAsync(10, 10);
        if (head == FasteningHead.Pickup)
        {
            io.SetOutput(OutputIo.PickupHeadDown, true);
            io.SetInput(InputIo.PickupHeadUp, false);
            io.SetInput(InputIo.PickupHeadDown, true);
            await gantry.MoveZAsync(settings.PickupPosition.Z);
            io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        }
        else
        {
            io.SetOutput(OutputIo.ShootingEscapeForward, true);
        }
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        Assert.Equal(head == FasteningHead.Pickup
            ? BoltFasteningState.WaitingForPickupFeeder
            : BoltFasteningState.WaitingForShootingFeeder, station.State());

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var continued = false;
        io.OutputChanged += (output, value) =>
        {
            if (head == FasteningHead.Pickup
                ? output == OutputIo.PickupHeadDown && !value
                : output == OutputIo.ShootBolt && value)
            {
                continued = true;
                stop.Cancel();
            }
        };
        var run = station.RunAsync(new BoltFasteningRecipe(), stop.Token);
        try
        {
            if (head == FasteningHead.Pickup)
                io.SetInput(InputIo.PickupHeadVacuumDetected, true);
            else
            {
                io.SetInput(InputIo.ShootingEscapeBackward, false);
                io.SetInput(InputIo.ShootingEscapeForward, true);
            }
            Assert.True(await WaitUntilAsync(() => continued, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stop.Cancel();
            await run;
        }
        Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.False(io.GetInput(InputIo.ShootingFeederBoltDetected));
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
    }

    private static BoltPoint Bolt(
        int number,
        FasteningHead head,
        double x,
        double y) => new()
        {
            Number = number,
            Head = head,
            X = x,
            Y = y,
        };

    private static BoltHeadSettings HeadSettings() => new()
    {
        UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
        LowerRightLocatingPin = new AxisPosition { X = 100, Y = 0 },
    };

}
