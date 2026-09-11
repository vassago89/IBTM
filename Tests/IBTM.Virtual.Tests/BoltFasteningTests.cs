using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
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
    public void AdcConnectionMustMatchBothRequestedSettings()
    {
        // Constructing a SerialPort does not open it or access hardware.
        using var port = new SerialPort("COM4", 19200);
        AdcBus.VerifyConnectionSettings(port, "com4", 19200);
        var wrongPort = Assert.Throws<InvalidOperationException>(
            () => AdcBus.VerifyConnectionSettings(port, "COM3", 19200));
        Assert.Contains("COM4", wrongPort.Message);
        Assert.Contains("COM3", wrongPort.Message);
        Assert.Throws<InvalidOperationException>(
            () => AdcBus.VerifyConnectionSettings(port, "COM4", 9600));
    }

    [Fact]
    public async Task AdcConnectionEditsApplyWhenHeadsAreRecreated()
    {
        var settings = new HantasSettings { PortName = "Virtual", BaudRate = 19200 };
        var bus = new VirtualAdcBus();
        var pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress);
        var shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress);
        byte expectedSlave = 0;
        var expectedBaudRate = 19200;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit)
            {
                Assert.Equal(expectedSlave, frame[0]);
                Assert.Equal(expectedBaudRate, bus.BaudRate);
            }
        };

        await pickup.CheckReadyAsync();
        settings.PortName = "COM5";
        settings.BaudRate = 115200;
        settings.PickupSlaveAddress = 2;
        settings.ShootingSlaveAddress = 3;

        foreach (var head in new[] { pickup, shooting })
        {
            await head.CheckReadyAsync();
            await head.ResetAsync();
            bus.Close();
            await head.ResetAsync();
            expectedSlave++;
        }

        bus.Close();
        expectedBaudRate = settings.BaudRate;
        pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress);
        shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress);
        foreach (var head in new[] { pickup, shooting })
        {
            await head.CheckReadyAsync();
            expectedSlave++;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcPreservesTheOperationFailureWhenStopAlsoFails(bool reverse)
    {
        var bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 0);
        var operationError = new IOException("Start response lost.");
        var stopError = new IOException("Stop response lost.");
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart)
            {
                throw BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) == 1 ? operationError : stopError;
            }
        };

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => reverse ? head.RunReverseAsync(CancellationToken.None) : head.TightenAsync());
        Assert.Equal(new[] { operationError, stopError }, error.InnerExceptions);
    }

    [Fact]
    public async Task AdcDisconnectedRequestsFailClearlyAndCancelledReadinessDoesNotOpenTheBus()
    {
        var settings = new HantasSettings();
        Assert.Equal((byte)0, settings.PickupSlaveAddress);
        Assert.Equal((byte)1, settings.ShootingSlaveAddress);
        using var bus = new AdcBus(settings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ReadDeviceInformationAsync(0));
        Assert.Contains("ADC is not connected", error.Message);

        var virtualBus = new VirtualAdcBus();
        var head = new AdcBoltHead(virtualBus, settings, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => head.CheckReadyAsync(cancellation.Token));
        Assert.False(virtualBus.IsOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualReverseStopsOnReleaseOrCommunicationFailureWithoutACompletionResult(
        bool communicationFailure)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 2);
        var started = false;
        var stops = 0;
        var writes = new List<(byte Slave, ushort Address, ushort Value)>();
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            var address = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister)
            {
                var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
                writes.Add((frame[0], address, value));
                if (address == (ushort)AdcRemoteRegister.RemoteStart)
                {
                    if (value == 0)
                        stops++;
                    else
                        started = true;
                }
            }

            if (communicationFailure
                && started
                && stops == 0
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

        Assert.Equal(
            new (byte Slave, ushort Address, ushort Value)[]
            {
                (2, (ushort)AdcRemoteRegister.Direction, (ushort)AdcDirection.Loosening),
                (2, (ushort)AdcRemoteRegister.RemoteStart, 1),
                (2, (ushort)AdcRemoteRegister.RemoteStart, 0),
            },
            writes);
        Assert.Equal(1, stops);
        Assert.False((await bus.ReadControllerStatusAsync(2)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(2)).EventCount);
        Assert.True((await head.TightenAsync()).Success); // Forward explicitly restores its own direction.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerialCancellationDrainsTheNativeOperationEvenWhenAbortFails(bool abortFails)
    {
        using var cancellation = new CancellationTokenSource();
        var nativeIo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortFailure = new IOException("Native serial abort failed.");
        Action abort = () =>
        {
            abortCalled.SetResult();
            if (abortFails)
                throw abortFailure;
        };
        var wait = AdcBus.AwaitSerialIoAsync(nativeIo.Task, abort, cancellation.Token);
        cancellation.Cancel();
        await abortCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wait.IsCompleted); // A following bus request must not overlap the aborted native IO.
        nativeIo.SetException(new IOException("Native serial IO aborted."));
        if (abortFails)
            Assert.Same(abortFailure, await Assert.ThrowsAsync<IOException>(() => wait));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("crc")]
    [InlineData("address")]
    [InlineData("write-response")]
    [InlineData("controller-error")]
    public async Task AdcRawReceiveLogsBytesBeforeResponseValidation(string responseKind)
    {
        var function = responseKind switch
        {
            "controller-error" => (AdcFunctionCode)0x84,
            "write-response" => AdcFunctionCode.WriteSingleRegister,
            _ => AdcFunctionCode.ReadInputRegisters,
        };
        byte[] data = responseKind switch
        {
            "controller-error" => [0x02],
            "write-response" => [0x0F, 0xA3, 0x00, 0x00],
            _ => [0x02, 0x12, 0x34],
        };
        var frame = AdcRtuFrame.Build((byte)(responseKind == "address" ? 1 : 0), function, data);
        if (responseKind == "crc")
            frame[^1] ^= 0xFF;
        using var stream = new AdcResponseStream(frame);
        var chunks = new List<byte[]>();
        var reading = ReadAdcResponseAsync(stream, chunks.Add, CancellationToken.None);

        if (responseKind == "valid")
            Assert.Equal(frame, await reading.WaitAsync(TimeSpan.FromSeconds(2)));
        else if (responseKind == "controller-error")
            Assert.Contains(
                "IllegalAddress",
                (await Assert.ThrowsAsync<IOException>(() => reading)).Message);
        else
            await Assert.ThrowsAsync<InvalidDataException>(
                () => reading.WaitAsync(TimeSpan.FromSeconds(2)));

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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reading.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.Aborted);
        Assert.Equal(partial[..receivedCount], chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task AdcRawCaptureKeepsEchoAndLateResponseUntilDeadline()
    {
        byte[] echo = [0x00, 0x11, 0xC1, 0xBC];
        var response = AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [0x02, 0x01, 0xFF]);
        byte[] incoming = [.. echo, .. response, 0xAB];
        using var stream = new AdcResponseStream(incoming, pauseAtByte: echo.Length, delayMilliseconds: 1100);
        var chunks = new List<byte[]>();
        var capture = AdcBus.CaptureResponseAsync(
            stream,
            stream.Abort,
            chunks.Add,
            2200,
            CancellationToken.None);

        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(capture.IsCompleted);
        Assert.Equal(incoming, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.Equal(incoming, await capture.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.Aborted);
    }

    [Fact]
    public async Task AdcRawCaptureCancellationPreservesLoggedBytesAndAbortsRead()
    {
        byte[] echo = [0x00, 0x11, 0xC1, 0xBC];
        using var stream = new AdcResponseStream(echo);
        using var cancellation = new CancellationTokenSource();
        var chunks = new List<byte[]>();
        var capture = AdcBus.CaptureResponseAsync(
            stream,
            stream.Abort,
            chunks.Add,
            3000,
            cancellation.Token);

        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => capture.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.Aborted);
        Assert.Equal(echo, chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task AdcRawCaptureSendsOnlyDeviceInformationAndReturnsFullFrame()
    {
        IAdcBus bus = new VirtualAdcBus();
        bus.Open("Virtual", 57600);
        var frames = new List<(AdcFrameDirection Direction, byte[] Bytes)>();
        bus.FrameTransferred += (direction, bytes) => frames.Add((direction, bytes));

        var captured = await bus.CaptureDeviceInformationAsync(0, 20);

        Assert.Equal(2, frames.Count);
        Assert.Equal(AdcFrameDirection.Transmit, frames[0].Direction);
        Assert.Equal(new byte[] { 0x00, 0x11, 0xC1, 0xBC }, frames[0].Bytes);
        Assert.Equal(AdcFrameDirection.Receive, frames[1].Direction);
        Assert.Equal(frames[1].Bytes, captured);
    }

    private static Task<byte[]> ReadAdcResponseAsync(
        AdcResponseStream stream,
        Action<byte[]> received,
        CancellationToken cancellationToken)
    {
        return AdcBus.ReadResponseAsync(
            stream,
            stream.Abort,
            received,
            0,
            AdcFunctionCode.ReadInputRegisters,
            cancellationToken);
    }

    private sealed class AdcResponseStream(
        byte[] bytes,
        int pauseAtByte = -1,
        int delayMilliseconds = 0) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource<int> _pending = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Aborted { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position == pauseAtByte)
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
            }

            if (Position < Length)
                return await base.ReadAsync(buffer[..1], cancellationToken);
            Waiting.TrySetResult();
            return await _pending.Task; // Model Windows native IO ignoring cancellation.
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
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
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
        Assert.False(head.HasPendingResult); // No local operation, but the physical head is running.
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(4));
        Assert.Equal((ushort)3, (await bus.ReadControllerStatusAsync(1)).Preset);
        await Task.Delay(300);
        Assert.True((await bus.ReadControllerStatusAsync(1)).Running);
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
        Assert.Equal((ushort)0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        await head.CheckReadyAsync();
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);
    }

    [Fact]
    public void RecoveryPreservesMeasuredResultsUntilExplicitlyUnchecked()
    {
        var io = new VirtualIoService(Outputs(new ConveyorHardwareSettings()), new MachineOptions());
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

        (HeatSinkSlot HeatSink, int Number, FasteningPass Pass, bool Completed)[] items = [
            (
                HeatSinkSlot.HeatSink1,
                1,
                FasteningPass.Pcb,
                true),
            (
                HeatSinkSlot.HeatSink1,
                4,
                FasteningPass.IpmSeating,
                true),
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

        work.PrepareRecovery([(HeatSinkSlot.HeatSink1, 1, FasteningPass.Pcb, false),]);

        Assert.Empty(first.PcbBoltResults);
        Assert.Same(finalNg, first.IpmFinalResults[2]);
        Assert.Equal(AssemblyResult.Ng, first.FasteningResult);
        Assert.True(work.HasNg);

        work.PrepareRecovery([(HeatSinkSlot.HeatSink1, 2, FasteningPass.IpmFinal, false),]);

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
    public async Task FailedFasteningPreparationStillStopsTheHead(AdcFunctionCode failingFunction)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1);
        await head.CheckReadyAsync();
        var failed = false;
        var stops = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit)
                return;
            if (frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart
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
        Assert.False(head.HasPendingResult);
        Assert.Equal(0, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.True((await head.TightenAsync()).Success);
        Assert.Equal(2, stops);
        Assert.False(head.HasPendingResult);
    }

    [Fact]
    public async Task ControllerErrorStopsUntilReset()
    {
        IAdcBus bus = new VirtualAdcBus();
        var virtualBus = (VirtualAdcBus)bus;
        var head = new AdcBoltHead(bus, new HantasSettings(), 2);
        await head.CheckReadyAsync();
        virtualBus.SetNextFasteningResult(2, AdcEventStatus.Error);

        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());

        Assert.False(head.HasPendingResult);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(2)).EventCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());

        virtualBus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.SetDirectionAsync(2, AdcDirection.Loosening);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StartAsync(2);
        Assert.Equal(AdcEventStatus.Error, (await bus.ReadFasteningResultAsync(2)).Status);
        await bus.StopAsync(2);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.TightenAsync());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFasteningStopsAndPreservesTheNextResult(bool timedOut)
    {
        IAdcBus bus = new VirtualAdcBus();
        var settings = new HantasSettings();
        var head = new AdcBoltHead(bus, settings, 1);
        await head.SelectPresetAsync(3);
        Assert.True((await head.TightenAsync()).Success);
        using var stop = new CancellationTokenSource();
        if (timedOut)
            settings.FasteningTimeoutMilliseconds = 20;
        var tightening = head.TightenAsync(stop.Token);

        Assert.True(head.HasPendingResult);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        ((VirtualAdcBus)bus).SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        if (timedOut)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => tightening);
        }
        else
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tightening);
        }

        Assert.Null(await head.ReadPendingResultAsync());
        await Task.Delay(300);
        Assert.Equal(1, (await bus.ReadFasteningResultAsync(1)).EventCount);
        Assert.True(head.HasPendingResult);
        Assert.False((await bus.ReadControllerStatusAsync(1)).Running);

        settings.FasteningTimeoutMilliseconds = 15_000;
        Assert.False((await head.TightenAsync()).Success);
        var completed = await bus.ReadFasteningResultAsync(1);
        Assert.Equal(2, completed.EventCount);
        Assert.Equal(3, completed.Preset);
        Assert.False(head.HasPendingResult);
        Assert.True((await head.TightenAsync()).Success);
    }

    [Fact]
    public async Task ShootingFeederKeepsTheNextBoltReady()
    {
        var io = new VirtualIoService(new BoltFeederHardwareSettings().Outputs, new MachineOptions());
        _ = new VirtualMachine(io, []);
        var feeder = new ShootingBoltFeeder(
            io,
            new BoltFeederSettings { ShootingTimeoutMilliseconds = 500, });
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task FasteningResumeKeepsTheResultWithItsCarrierBoltAndPass(
        bool replaceCarrier,
        bool markCompleted)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 0,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new MachineOptions()) { AutoResponseEnabled = false };
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        var bus = new VirtualAdcBus();
        var pickupHead = new AdcBoltHead(bus, new HantasSettings(), 1);
        var gantry = new BoltFasteningGantry(
            new AdcBoltHead(bus, new HantasSettings(), 2),
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100 },
            });
        var work = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)],
        };
        var station = new BoltFasteningStation(
            gantry,
            work,
            new PickupBoltFeeder(io, new()),
            new ShootingBoltFeeder(io, new()),
            () => layout);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.PickupHeadDown, true);
        io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        var originalAssembly = work.Assembly(HeatSinkSlot.HeatSink1);
        originalAssembly.RecordIpmSeating(1, new(true, 1));
        Assert.Equal(BoltFasteningState.FinalizingIpm, station.State());

        var responseError = new IOException("Completed fastening response lost.");
        var loseResult = true;
        var starts = 0;
        using var resumedStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var resuming = false;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Receive
                && frame[1] == (byte)AdcFunctionCode.ReadInputRegisters
                && frame[2] == AdcFasteningResult.RegisterCount * 2
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3)) != 0)
            {
                if (loseResult)
                {
                    loseResult = false;
                    throw responseError;
                }

                if (resuming
                    && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(3)) == (replaceCarrier ? 2 : 1))
                {
                    resumedStop.Cancel();
                }
            }

            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) != 0)
                    starts++;
                else if (resuming)
                    resumedStop.Cancel();
            }
        };
        bus.SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Same(responseError, await Assert.ThrowsAsync<IOException>(
            () => station.RunAsync(new(), firstStop.Token)));
        Assert.True(pickupHead.HasPendingResult);
        Assert.Empty(originalAssembly.IpmFinalResults);
        Assert.False((await ((IAdcBus)bus).ReadControllerStatusAsync(1)).Running);

        if (replaceCarrier)
        {
            io.SetInput(InputIo.BoltFasteningCarrierPresent, false);
            io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
            work.Assembly(HeatSinkSlot.HeatSink1).RecordIpmSeating(1, new(true, 1));
        }
        else
        {
            // Changing an earlier pass must not reassign the outstanding final-pass result.
            station.PrepareRecovery([
                (HeatSinkSlot.HeatSink1, 1, FasteningPass.IpmSeating, false),
                (HeatSinkSlot.HeatSink1, 1, FasteningPass.IpmFinal, markCompleted),
            ]);
            Assert.Equal(!markCompleted, pickupHead.HasPendingResult);
            if (markCompleted)
            {
                Assert.Equal(BoltResultSource.Manual, originalAssembly.IpmFinalResults[1].Source);
                Assert.NotEqual(BoltFasteningState.FinalizingIpm, station.State());
                return;
            }

            io.SetInput(InputIo.PickupHeadDown, false);
            io.SetInput(InputIo.PickupHeadUp, true);
            Assert.Equal(BoltFasteningState.LoweringForIpmFinal, station.State());
            Assert.Equal(1, station.ActiveBolt()!.Number);
        }

        resuming = true;
        await station.RunAsync(new(), resumedStop.Token);
        var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(replaceCarrier, assembly.IpmFinalResults[1].Success);
        Assert.Equal(replaceCarrier ? 2 : 1, starts);
        Assert.False(pickupHead.HasPendingResult);
        if (replaceCarrier)
        {
            Assert.NotSame(originalAssembly, assembly);
            Assert.Empty(originalAssembly.IpmFinalResults);
        }
        else
        {
            Assert.Empty(assembly.IpmSeatingResults);
        }
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task FasteningPreservesPassOrderAndCarrierResults()
    {
        var settings = new BoltFasteningSettings
        {
            Motion = new MotionSettings { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
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
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootBolt && value)
            {
                // The tube pulse may finish before the sequence's next poll.
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            }
        };
        var bus = new VirtualAdcBus();
        var presets = new Dictionary<byte, ushort>();
        var tightenings = new List<(byte Head, ushort Preset)>();
        Action? afterStop = null;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit
                || frame[1] != (byte)AdcFunctionCode.WriteSingleRegister)
                return;

            var register = (AdcRemoteRegister)BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
            if (register == AdcRemoteRegister.Preset)
                presets[frame[0]] = value;
            else if (register == AdcRemoteRegister.RemoteStart && value != 0)
                tightenings.Add((frame[0], presets[frame[0]]));
            else if (register == AdcRemoteRegister.RemoteStart)
                afterStop?.Invoke();
        };
        var connection = new HantasSettings { PortName = "Virtual" };
        var pickupHead = new AdcBoltHead(bus, connection, 1);
        var shootingHead = new AdcBoltHead(bus, connection, 2);
        using var motion = new VirtualMotionService(
            settings.Motion,
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new());
        var gantry = new BoltFasteningGantry(
            shootingHead,
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
                LowerRightLocatingPin = new AxisPosition { X = 100, Y = 0 },
            });
        var movedWithLoweredCylinder = false;
        motion.PositionChanged += (_, _, _) =>
            movedWithLoweredCylinder |= motion.IsMovingHorizontal
                && !gantry.CanMoveHorizontal;
        var work = new BoltFasteningWork(ConveyorStation.BoltFastening(io));
        var pickupFeeder = new PickupBoltFeeder(io, new());
        var shootingFeeder = new ShootingBoltFeeder(io, new());
        var layout = new PcbLayout

        {

            BoltPoints = [
                Bolt(1, FasteningHead.Pickup, 20, 30),
                Bolt(2, FasteningHead.Shooting, 20, 30),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 30, Y = 40 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 30, Y = 40 },
            ],
        };
        var station = new BoltFasteningStation(gantry, work, pickupFeeder, shootingFeeder, () => layout);
        var recipe = new BoltFasteningRecipe { PcbPreset = 4, IpmSeatingPreset = 3, IpmFinalPreset = 5 };

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToSafeZAsync();
        await gantry.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetOutput(OutputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        using var feederCancellation = new CancellationTokenSource();
        var feederRuns = Task.WhenAll(
            pickupFeeder.RunAsync(feederCancellation.Token),
            shootingFeeder.RunAsync(feederCancellation.Token));
        try
        {
            using (var stopAfterPickup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                void StopWithPickedBolt(InputIo input, bool value)
                {
                    if (input == InputIo.PickupHeadVacuumDetected && value)
                        stopAfterPickup.Cancel();
                }

                io.InputChanged += StopWithPickedBolt;
                try
                {
                    await station.RunAsync(recipe, stopAfterPickup.Token);
                }
                finally
                {
                    io.InputChanged -= StopWithPickedBolt;
                }

                Assert.True(gantry.PickupBoltLoaded);
                Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
                Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
                Assert.Equal(settings.PickupPosition.Z, motion.GetPosition().Z);
                Assert.DoesNotContain(tightenings, item => item.Head == 1);
                Assert.False(work.Completed);
            }

            using var cancellation = new CancellationTokenSource();
            var resumedRun = station.RunAsync(recipe, cancellation.Token);
            try
            {
                Assert.True(
                    await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10)),
                    $"State={station.State()}, Error={resumedRun.Exception?.GetBaseException().Message}");
            }
            finally
            {
                cancellation.Cancel();
                await resumedRun;
            }

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
            Assert.False(pickupHead.HasPendingResult);
            Assert.False(shootingHead.HasPendingResult);
            Assert.False(movedWithLoweredCylinder);
            Assert.True(gantry.CanMoveHorizontal);
            Assert.True(motion.IsAtHorizontalZ);
            Assert.Equal(
                new (byte Head, ushort Preset)[] { (2, 4), (2, 4), (1, 3), (1, 3), (1, 5), (
                    1,
                    5), },
                tightenings);
            // A replaced carrier must never inherit the previous carrier's in-flight result.
            io.SetInput(InputIo.BoltFasteningCarrierPresent, false);
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
            io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
            var previousAssembly = work.Assembly(HeatSinkSlot.HeatSink1);
            using var carrierChange = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            afterStop = () =>
            {
                io.SetInput(InputIo.BoltFasteningCarrierPresent, false);
                io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
                carrierChange.Cancel();
            };
            await station.RunAsync(recipe, carrierChange.Token);
            Assert.Single(previousAssembly.PcbBoltResults);
            Assert.Empty(work.Assemblies);
            Assert.False(work.Completed);
        }
        finally
        {
            feederCancellation.Cancel();
            await feederRuns;
        }
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
            Outputs(
                new BoltFasteningHardwareSettings(),
                new BoltFeederHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(
            settings.Motion,
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new OperationCancellation());
        var bus = new VirtualAdcBus();
        var gantry = new BoltFasteningGantry(
            new AdcBoltHead(bus, new HantasSettings(), 2),
            new AdcBoltHead(bus, new HantasSettings(), 1),
            io,
            motion,
            settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100 }, });
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, head, 10, 10)],
        };
        var feederSettings = new BoltFeederSettings();
        var station = new BoltFasteningStation(
            gantry,
            new BoltFasteningWork(ConveyorStation.BoltFastening(io)),
            new PickupBoltFeeder(io, feederSettings),
            new ShootingBoltFeeder(io, feederSettings),
            () => layout);
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToXYAsync(10, 10);
        if (head == FasteningHead.Pickup)
        {
            io.SetOutput(OutputIo.PickupHeadUp, false);
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
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        Assert.Equal(
            head == FasteningHead.Pickup
                ? BoltFasteningState.WaitingForPickupFeeder
                : BoltFasteningState.WaitingForShootingFeeder,
            station.State());

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var continued = false;
        io.OutputChanged += (output, value) =>
        {
            if (head == FasteningHead.Pickup
                ? output == OutputIo.PickupHeadUp && value
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

    private static BoltPoint Bolt(int number, FasteningHead head, double x, double y)
    {
        return new()
        {
            Number = number,
            Head = head,
            X = x,
            Y = y,
        };
    }

    private static BoltHeadSettings HeadSettings()
    {
        return new()
        {
            UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
            LowerRightLocatingPin = new AxisPosition { X = 100, Y = 0 },
        };
    }

}
