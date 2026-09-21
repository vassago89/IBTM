using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
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
using IBTM.Storage;

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
        var settings = new HantasSettings { PickupPortName = "Virtual", PickupBaudRate = 19200 };
        var bus = new VirtualAdcBus();
        var pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        var shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
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
        settings.PickupPortName = "COM5";
        settings.PickupBaudRate = 115200;
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
        expectedBaudRate = settings.PickupBaudRate;
        pickup = new AdcBoltHead(bus, settings, settings.PickupSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
        shooting = new AdcBoltHead(bus, settings, settings.ShootingSlaveAddress, settings.PickupPortName, settings.PickupBaudRate);
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
        var head = new AdcBoltHead(bus, new HantasSettings(), 0, "Virtual", 115200);
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
        var head = new AdcBoltHead(virtualBus, settings, 0, "Virtual", 115200);
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
        var head = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
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
    [InlineData(AdcResponseKind.Valid)]
    [InlineData(AdcResponseKind.InvalidCrc)]
    [InlineData(AdcResponseKind.WrongAddress)]
    [InlineData(AdcResponseKind.WriteResponse)]
    [InlineData(AdcResponseKind.ControllerError)]
    public async Task AdcRawReceiveLogsBytesBeforeResponseValidation(AdcResponseKind responseKind)
    {
        var function = responseKind switch
        {
            AdcResponseKind.ControllerError => (AdcFunctionCode)0x84,
            AdcResponseKind.WriteResponse => AdcFunctionCode.WriteSingleRegister,
            _ => AdcFunctionCode.ReadInputRegisters,
        };
        byte[] data = responseKind switch
        {
            AdcResponseKind.ControllerError => [0x02],
            AdcResponseKind.WriteResponse => [0x0F, 0xA3, 0x00, 0x00],
            _ => [0x02, 0x12, 0x34],
        };
        var frame = AdcRtuFrame.Build((byte)(responseKind == AdcResponseKind.WrongAddress ? 1 : 0), function, data);
        if (responseKind == AdcResponseKind.InvalidCrc)
            frame[^1] ^= 0xFF;
        using var stream = new AdcResponseStream(frame);
        var chunks = new List<byte[]>();
        var reading = ReadAdcResponseAsync(stream, chunks.Add, CancellationToken.None);

        if (responseKind == AdcResponseKind.Valid)
            Assert.Equal(frame, await reading.WaitAsync(TimeSpan.FromSeconds(2)));
        else if (responseKind == AdcResponseKind.ControllerError)
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

    [Fact]
    public async Task AdcReadinessUsesLiveRunAndDoesNotCallReverseAFastening()
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
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
        Assert.Equal(2, statusReads); // Selection is confirmed from the current preset register.
        Assert.Equal((ushort)3, (await bus.ReadControllerStatusAsync(1)).Preset);

        await bus.SetDirectionAsync(1, AdcDirection.Loosening);
        await bus.StartAsync(1);
        var running = await bus.ReadControllerStatusAsync(1);
        Assert.True(running.Running);
        Assert.False(running.Ready);
        Assert.Equal(AdcDirection.Loosening, running.Direction);
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.CheckReadyAsync());
        Assert.False(head.HasPendingResult); // No local operation, but the physical head is running.
        await Assert.ThrowsAsync<InvalidOperationException>(() => head.SelectPresetAsync(3));
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

    [Theory]
    [InlineData(AdcFunctionCode.ReadInputRegisters)]
    [InlineData(AdcFunctionCode.WriteSingleRegister)]
    public async Task FailedFasteningPreparationStillStopsTheHead(AdcFunctionCode failingFunction)
    {
        IAdcBus bus = new VirtualAdcBus();
        var head = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
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

        var fed = false;
        Task FeedAsync(CancellationToken token)
        {
            fed = true;
            return Task.CompletedTask;
        }
        await Assert.ThrowsAsync<IOException>(() => head.TightenAsync(feedAsync: FeedAsync));
        Assert.False(fed);
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
        var head = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
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
        var selected = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);
        var other = new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200);
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
        var head = new AdcBoltHead(bus, settings, 1, "Virtual", 115200);
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

    [Theory]
    [InlineData(InputIo.ShootingTubeBoltDetected, true)]
    [InlineData(InputIo.ShootingTubeBoltDetected, false)]
    public async Task ShootingDetectionUsesItsOwnTimeoutAndStopsTheShot(InputIo input, bool value)
    {
        var settings = new BoltFasteningSettings { ShootingDetectionTimeoutMilliseconds = 50 };
        var io = new VirtualIoService(
            new BoltFasteningHardwareSettings().Outputs,
            new MachineOptions { TimeoutMilliseconds = 5 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var bus = new VirtualAdcBus();
        var gantry = VirtualTest.CreateFastening(
            new AdcBoltHead(bus, new(), 2, "Virtual", 115200),
            new AdcBoltHead(bus, new(), 1, "Virtual", 115200),
            io, motion, settings, new());
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
        };
        if (!value)
            io.SetInput(InputIo.ShootingTubeBoltDetected, true);

        var error = await Assert.ThrowsAsync<IoTimeoutException>(() => value
            ? gantry.ShootBoltAsync()
            : gantry.WaitForShootingTubeClearAsync(CancellationToken.None));

        Assert.Contains(input.GetDescription(), error.Message);
        Assert.Contains("timeout (50 ms)", error.Message);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShootingArrivalUsesSecondsWithoutVacuumAndStopCancelsDelay(bool stopDuringDelay)
    {
        var settings = new BoltFasteningSettings
        {
            ShootingArrivalDelaySeconds = 0.2,
            ShootingDetectionTimeoutMilliseconds = 500,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ShootingArrivalDelaySeconds = double.NaN);
        var io = new VirtualIoService(new BoltFasteningHardwareSettings().Outputs, new())
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new());
        var bus = new VirtualAdcBus();
        var gantry = VirtualTest.CreateFastening(
            new AdcBoltHead(bus, new(), 2, "Virtual", 115200), new AdcBoltHead(bus, new(), 1, "Virtual", 115200),
            io, motion, settings, new());
        io.Initialize();
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var shot = gantry.ShootBoltAsync(stop.Token);
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(shot.IsCompleted);
        var arrival = Stopwatch.StartNew();
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
        try
        {
            if (stopDuringDelay)
            {
                // Passage was observed, but the arrival delay has not completed.
                await Task.Delay(30);
                Assert.False(shot.IsCompleted);
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shot);
            }
            else
            {
                await shot.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.True(arrival.Elapsed >= TimeSpan.FromSeconds(0.18));
            }
            Assert.False(io.GetInput(InputIo.ShootingHeadVacuumDetected));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
        }
        finally
        {
            stop.Cancel();
            await shot.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ShootingFeederKeepsTheNextBoltReady()
    {
        var io = new VirtualIoService(
            Outputs(new BoltFeederHardwareSettings(), new BoltFasteningHardwareSettings()), new MachineOptions());
        _ = new VirtualMachine(io, []);
        var feeder = new BoltFeederUnit(
            FasteningHead.Shooting,
            io,
            new BoltFeederSettings { ShootingTimeoutMilliseconds = 500, });
        var refillCount = 0;
        var runCount = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingFeederOff && !value)
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
        feeder.Stop();
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        using var cancellation = new CancellationTokenSource();
        var run = feeder.RunAsync(cancellation.Token);
        var firstBolt = await WaitUntilAsync(
            () => runCount == 1
                && feeder.State == BoltFeederState.BoltReady
                && io.GetOutput(OutputIo.ShootingFeederOff),
            TimeSpan.FromSeconds(1));
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, true);
        // The independent feeder refills as soon as detection clears, even while escape is forward.
        Assert.True(await WaitUntilAsync(
            () => runCount == 2 && !io.GetOutput(OutputIo.ShootingFeederOff),
            TimeSpan.FromSeconds(1)));
        var nextBolt = await WaitUntilAsync(
            () => runCount == 2
                && refillCount == 2
                && feeder.State == BoltFeederState.BoltReady
                && io.GetOutput(OutputIo.ShootingFeederOff),
            TimeSpan.FromSeconds(1));
        Assert.True(io.GetInput(InputIo.ShootingEscapeForward));
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingEscapeForward, false);
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        Assert.Equal(2, runCount);

        cancellation.Cancel();
        await run;

        Assert.True(firstBolt);
        Assert.True(nextBolt);
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, false, false)]
    [InlineData(FasteningHead.Pickup, false, false)]
    [InlineData(FasteningHead.Pickup, true, false)]
    [InlineData(FasteningHead.Shooting, true, false)]
    [InlineData(FasteningHead.Pickup, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Motion)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Supply)]
    [InlineData(FasteningHead.Shooting, false, false, false, false, ShootingPreparationFailure.Stop)]
    public async Task IoFasteningStartsBeforeDescentAndStopsAfterCompletionOrFeedFailure(
        FasteningHead selectedHead,
        bool stopDuringDescent,
        bool missingDownFeedback,
        bool loseTableUp = false,
        bool shootWithoutVacuum = false,
        ShootingPreparationFailure preparationFailure = ShootingPreparationFailure.None)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            ShootingArrivalDelaySeconds = 0.05,
            ShootingDetectionTimeoutMilliseconds = preparationFailure == ShootingPreparationFailure.Supply ? 50 : 2_000,
            Motion = new() { HorizontalSpeed = 200, ZSpeed = 20_000 },
            PickupPosition = new() { X = 100, Y = 100, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new BoltFeederHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings),
            new() { TimeoutMilliseconds = missingDownFeedback ? 100 : 2_000 })
        { AutoResponseEnabled = false };
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickup = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        using var shooting = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var bolt = selectedHead == FasteningHead.Shooting
            ? Bolt(1, selectedHead, 20, 30)
            : Bolt(1, selectedHead, 0, 0);
        var layout = new PcbLayout { BoltPoints = [bolt] };
        var station = new BoltFasteningStation(shooting,
            pickup,
            io,
            motion,
            settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 } },
            work,
            new BoltFeederUnit(FasteningHead.Pickup, io, new()),
            new BoltFeederUnit(FasteningHead.Shooting, io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        await gantry.MoveZAsync(selectedHead == FasteningHead.Shooting
            ? settings.SafeZ : settings.PickupHead.FasteningZ);
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupTableUp, selectedHead != FasteningHead.Pickup),
            (InputIo.PickupTableDown, selectedHead == FasteningHead.Pickup),
            (InputIo.PickupHeadVacuumDetected, true),
            (InputIo.ShootingHeadVacuumDetected, !shootWithoutVacuum),
            (InputIo.ShootingFeederBoltDetected, selectedHead == FasteningHead.Shooting),
            (InputIo.ShootingEscapeBackward, true));
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        var results = selectedHead == FasteningHead.Shooting ? assembly.PcbBoltResults : assembly.PickupBoltResults;
        var selected = selectedHead == FasteningHead.Pickup ? pickup : shooting;
        var (start, fasten, cylinder, up, down) = selectedHead == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, InputIo.PickupBoltFasten, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, InputIo.ShootingBoltFasten, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        var commands = new List<string>();
        var supplyCommands = new List<string>();
        var shotElapsed = new Stopwatch();
        motion.PositionChanged += (x, y, z) =>
        {
            if (motion.IsMoving)
            {
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
                if (preparationFailure == ShootingPreparationFailure.Motion && x > 0)
                    motion.Stop();
            }
        };
        var descending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        io.OutputChanged += (output, on) =>
        {
            Assert.NotEqual(OutputIo.ShootingFeederOff, output); // Only the feeder loop owns this output.
            if (output == OutputIo.ShootingEscapeForward)
            {
                if (on)
                {
                    Assert.True(io.GetInput(InputIo.ShootingFeederBoltDetected));
                }
                supplyCommands.Add(on ? "ESCAPE FORWARD" : "ESCAPE BACKWARD");
                io.SetInputs((InputIo.ShootingEscapeForward, on), (InputIo.ShootingEscapeBackward, !on));
            }
            else if (output == OutputIo.ShootBolt)
            {
                supplyCommands.Add(on ? "SHOOT ON" : "SHOOT OFF");
                if (on)
                {
                    Assert.True(io.GetInput(InputIo.ShootingEscapeForward));
                    Assert.False(motion.IsMoving);
                    Assert.Equal((bolt.X!.Value, bolt.Y!.Value, settings.ShootingHead.FasteningZ), motion.GetPosition());
                    shotElapsed.Restart();
                    if (preparationFailure == ShootingPreparationFailure.Stop)
                        stop.Cancel();
                    else if (preparationFailure != ShootingPreparationFailure.Supply)
                    {
                        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                        io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                    }
                }
            }
            else if (!on && output == OutputIo.PickupHeadVacuumPump)
                io.SetInput(InputIo.PickupHeadVacuumDetected, false);
            else if (!on && output == OutputIo.ShootingHeadVacuumPump)
                io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
            else if (output == start)
            {
                commands.Add(on ? "START ON" : "START OFF");
                if (on)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed);
                    Assert.Equal(settings.GetHead(selectedHead).FasteningZ, motion.GetPosition().Z);
                    if (selectedHead == FasteningHead.Shooting)
                    {
                        Assert.True(shotElapsed.IsRunning);
                        Assert.True(shotElapsed.Elapsed >= TimeSpan.FromSeconds(settings.ShootingArrivalDelaySeconds - 0.005));
                        Assert.Equal((bolt.X!.Value, bolt.Y!.Value, settings.ShootingHead.FasteningZ), motion.GetPosition());
                        Assert.False(motion.IsMoving);
                        Assert.Equal(!shootWithoutVacuum, io.GetInput(InputIo.ShootingHeadVacuumDetected));
                        Assert.False(io.GetOutput(OutputIo.ShootBolt));
                        Assert.Equal(new[] { "ESCAPE FORWARD", "SHOOT ON", "SHOOT OFF", "ESCAPE BACKWARD" }, supplyCommands);
                    }
                }
                io.SetInput(fasten, on); // STOP-induced OFF must not become a successful result.
            }
            else if (output == cylinder)
            {
                if (on)
                {
                    Assert.True(io.GetOutput(start));
                    commands.Add("DOWN");
                    io.SetInputs((up, false), (down, false));
                    descending.TrySetResult();
                }
                else
                {
                    io.SetInputs((up, true), (down, false));
                    if (results.ContainsKey(1))
                        stop.Cancel();
                }
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            if (preparationFailure != ShootingPreparationFailure.None)
            {
                if (preparationFailure == ShootingPreparationFailure.Motion)
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
                else if (preparationFailure == ShootingPreparationFailure.Supply)
                    await Assert.ThrowsAsync<IoTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
                else
                    await run.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(preparationFailure != ShootingPreparationFailure.Motion, shotElapsed.IsRunning);
                if (preparationFailure == ShootingPreparationFailure.Motion)
                    Assert.Empty(supplyCommands);
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
                Assert.False(motion.IsMoving);
                Assert.Empty(commands);
                Assert.Empty(results);
                return;
            }
            await descending.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(new[] { "START ON", "DOWN" }, commands);
            Assert.True(io.GetOutput(start));
            Assert.False(run.IsCompleted);
            Assert.Empty(results);
            if (loseTableUp)
                io.SetInput(InputIo.PickupTableUp, false);
            else if (stopDuringDescent)
                stop.Cancel();
            else if (missingDownFeedback)
                io.SetInput(fasten, false); // A complete FASTEN pulse cannot replace cylinder feedback.
            else
            {
                io.SetInput(down, true);
                io.SetInput(fasten, false);
            }

            if (loseTableUp)
                await Assert.ThrowsAsync<MotionInterlockException>(() => run);
            else if (missingDownFeedback)
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(new[] { "START ON", "DOWN", "START OFF" }, commands);
            Assert.False(io.GetOutput(start));
            if (stopDuringDescent || missingDownFeedback || loseTableUp)
            {
                Assert.Empty(results);
                Assert.True(selected.HasPendingResult);
                Assert.True(station.HasPendingResult);
                Assert.True(io.GetOutput(cylinder));
            }
            else
            {
                Assert.True(results[1].Success);
                Assert.Equal(BoltResultSource.IoAssumedOk, results[1].Source);
                Assert.Null(results[1].Torque);
            }
        }
        finally
        {
            stop.Cancel();
            await run.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedFasteningRetriesSameBoltWithoutEmptyingOrReset(bool useIo)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 10;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings),
            new())
        { AutoResponseEnabled = false };
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickupIo = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        using var shooting = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);
        var bus = new AdcProtocolTests.ControllerBus();
        IBoltHead pickup = useIo ? pickupIo : new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout { BoltPoints = [Bolt(1, FasteningHead.Pickup, 10, 10)] };
        var units = new UnitSettings();
        var station = new BoltFasteningStation(shooting,
            pickup,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100, Y = 100 },
            },
            work,
            new BoltFeederUnit(FasteningHead.Pickup, io, new()),
            new BoltFeederUnit(FasteningHead.Shooting, io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            units);
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupHeadUp, true),
            (InputIo.PickupHeadDown, false),
            (InputIo.PickupHeadVacuumDetected, true));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var interruptDescent = true;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadDown)
            {
                if (on && interruptDescent)
                {
                    io.SetInput(InputIo.PickupHeadUp, false);
                    stop.Cancel();
                    return;
                }
                io.SetInputs((InputIo.PickupHeadUp, !on), (InputIo.PickupHeadDown, on));
            }
        };
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());
        await station.RunAsync(stop.Token);
        interruptDescent = false;
        Assert.True(station.HasPendingResult);
        Assert.True(pickup.HasPendingResult);
        Assert.Empty(assembly.PickupBoltResults);

        // A new START keeps this carrier/bolt, including with the feeder OFF.
        units.PickupBoltFeeder = false;
        Assert.True(station.HasPendingResult);
        Assert.Empty(assembly.PickupBoltResults);
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown));
        if (!useIo)
            Assert.Equal(1, bus.StartWrites);
        var job = work.CurrentJob;
        var movedBeforeRestart = false;
        var restarted = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (!restarted)
                movedBeforeRestart = true;
        };
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                restarted = true;
                io.SetInput(InputIo.PickupBoltFasten, true);
                io.SetInput(InputIo.PickupBoltFasten, false);
            }
            if (output == OutputIo.PickupHeadDown && !on && assembly.PickupBoltResults.ContainsKey(1))
                finish.Cancel();
        };
        await station.RunAsync(finish.Token);
        Assert.False(movedBeforeRestart);
        Assert.Same(job, work.CurrentJob);
        Assert.Same(assembly, Assert.Single(work.Assemblies));
        Assert.True(work.Station.CarrierPresent);
        Assert.Equal(
            useIo ? BoltResultSource.IoAssumedOk : BoltResultSource.Controller,
            assembly.PickupBoltResults[1].Source);
        Assert.True(assembly.PickupBoltResults[1].Success);
        if (useIo)
            Assert.Null(assembly.PickupBoltResults[1].Torque);
        else
            Assert.Equal(2, bus.StartWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingResultStaysWithItsCarrierAndBolt(bool replaceCarrier)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 0,
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new MachineOptions())
        { AutoResponseEnabled = false };
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        var bus = new VirtualAdcBus();
        var pickupHead = new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200);

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)],
        };
        var station = new BoltFasteningStation(new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200),
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100, Y = 100 },
            },
            work,
            new BoltFeederUnit(FasteningHead.Pickup, io, new()),
            new BoltFeederUnit(FasteningHead.Shooting, io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.PickupHeadDown, false);
        io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadDown)
                io.SetInputs((InputIo.PickupHeadUp, !on), (InputIo.PickupHeadDown, on));
        };
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        var originalAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());

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
            () => station.RunAsync(firstStop.Token)));
        Assert.True(pickupHead.HasPendingResult);
        Assert.Empty(originalAssembly.PickupBoltResults);
        Assert.True(station.HasPendingResult);
        Assert.False((await ((IAdcBus)bus).ReadControllerStatusAsync(1)).Running);

        if (replaceCarrier)
        {
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            Assert.False(station.HasPendingResult);
        }
        else
        {
            io.SetInput(InputIo.PickupHeadDown, false);
            io.SetInput(InputIo.PickupHeadUp, true);
            Assert.Equal(BoltFasteningState.FasteningPickup, station.GetState());
            Assert.Equal(1, station.GetActiveBolt()!.Number);
        }

        resuming = true;
        await station.RunAsync(resumedStop.Token);
        var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        Assert.Equal(replaceCarrier, assembly.PickupBoltResults[1].Success);
        Assert.Equal(replaceCarrier ? 2 : 1, starts);
        Assert.False(pickupHead.HasPendingResult);
        if (replaceCarrier)
        {
            Assert.NotSame(originalAssembly, assembly);
            Assert.Empty(originalAssembly.PickupBoltResults);
        }
        else
        {
            Assert.Single(assembly.PickupBoltResults);
        }
    }

    [Fact]
    public async Task ShootingStandbyAndPickupTableFollowSingleFasteningCycle()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingArrivalDelaySeconds = 0.05,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 100, Y = 50, Z = 10 },
            ShootingHead = HeadSettings(),
            PickupHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        settings.ShootingHead.UpperLeftLocatingPin = new() { X = 300, Y = 400 };
        settings.ShootingHead.LowerRightLocatingPin = new() { X = 300, Y = 440 };
        settings.PickupHead.UpperLeftLocatingPin = new() { X = -50, Y = 250 };
        settings.PickupHead.LowerRightLocatingPin = new() { X = -50, Y = 210 };
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        using var motion = new VirtualMotionService(settings.Motion, operationCancellation: new(), horizontalZ: () => settings.SafeZ);
        var bus = new VirtualAdcBus();

        var layout = new PcbLayout
        {
            BoltPoints = [
                Bolt(1, FasteningHead.Shooting, 110, 220),
                Bolt(2, FasteningHead.Pickup, 115, 225),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 120, Y = 230 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 125, Y = 235 },
            ],
        };
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var station = new BoltFasteningStation(new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200),
            new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200),
            io,
            motion,
            settings,
            new() { UpperLeftLocatingPin = new() { X = 100, Y = 200 }, LowerRightLocatingPin = new() { X = 140, Y = 200 } },
            work,
            new BoltFeederUnit(FasteningHead.Pickup, io, new()),
            new BoltFeederUnit(FasteningHead.Shooting, io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new() { PickupBoltFeeder = false, ShootingBoltFeeder = false });
        var gantry = station;
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await station.SetPickupTableDownAsync(true, CancellationToken.None);
        await ((IAdcBus)bus).SelectPresetAsync(2, 4);
        await ((IAdcBus)bus).SelectPresetAsync(1, 5);
        var presets = new List<(byte Head, ushort Preset)>();
        var starts = new List<(byte Head, double X, double Y, double Z)>();
        var pickups = 0;
        var tableDescents = 0;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction != AdcFrameDirection.Transmit
                || frame[1] != (byte)AdcFunctionCode.WriteSingleRegister)
                return;
            var register = (AdcRemoteRegister)BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2));
            var value = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4));
            if (register == AdcRemoteRegister.Preset)
                presets.Add((frame[0], value));
            if (register != AdcRemoteRegister.RemoteStart || value == 0)
                return;
            var position = motion.GetPosition();
            starts.Add((frame[0], position.X, position.Y, position.Z));
            Assert.Equal(frame[0] == 2 ? BoltCylinderState.Up : BoltCylinderState.Down, gantry.PickupTablePosition);
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupTableDown && on)
            {
                tableDescents++;
                Assert.True(gantry.IsAtSafeZ());
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(2, starts.Count);
                Assert.All(work.Assemblies, assembly => Assert.Single(assembly.PcbBoltResults));
            }
            if (output == OutputIo.PickupHeadVacuumPump && on)
            {
                pickups++;
                Assert.Equal(BoltCylinderState.Down, gantry.PickupTablePosition);
                Assert.True(gantry.IsAtPickupPosition());
            }
        };
        motion.PositionChanged += (_, _, z) =>
        {
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(settings.SafeZ, z);
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(tableDescents > 0 && !work.Completed ? BoltCylinderState.Down : BoltCylinderState.Up,
                    gantry.PickupTablePosition);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((280d, 410d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
            Assert.Empty(starts);
            Assert.Equal(BoltCylinderState.Up, gantry.PickupTablePosition);
            io.SetInputs(
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink2Present, true));
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(1)));
            Assert.Empty(starts); // No descent while the carrier is still on the belt.
            await work.Station.SeatAsync(CancellationToken.None);
            Assert.True(await WaitUntilAsync(() => work.Completed || run.IsCompleted, TimeSpan.FromSeconds(8)));
            Assert.True(work.Completed, run.Exception?.ToString() ?? station.GetState().ToString());
            Assert.Equal(new (byte, double, double, double)[] {
                (2, 280, 410, 12), (2, 270, 420, 12), (1, -25, 235, 16), (1, -15, 225, 16),
            }, starts);
            Assert.Equal(new (byte, ushort)[] { (2, 1), (1, 1) }, presets);
            Assert.Equal(2, pickups);
            Assert.Equal(1, tableDescents);
            Assert.All(work.Assemblies, assembly => Assert.Single(assembly.PickupBoltResults));
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((280d, 410d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
            Assert.Equal(BoltCylinderState.Up, gantry.PickupTablePosition);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task FasteningPreservesHeadOrderAndCarrierResults()
    {
        var settings = new BoltFasteningSettings
        {
            ShootingArrivalDelaySeconds = 0.05,
            Motion = new MotionSettings { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new AxisPosition { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
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
        await ((IAdcBus)bus).SelectPresetAsync(2, 4);
        await ((IAdcBus)bus).SelectPresetAsync(1, 5);
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
        var connection = new HantasSettings { PickupPortName = "Virtual" };
        var pickupHead = new AdcBoltHead(bus, connection, 1, "Virtual", 115200);
        var shootingHead = new AdcBoltHead(bus, connection, 2, "Virtual", 115200);
        using var motion = new VirtualMotionService(
            settings.Motion,
            horizontalZ: () => settings.SafeZ,
            operationCancellation: new());

        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var pickupFeeder = new BoltFeederUnit(FasteningHead.Pickup, io, new());
        var shootingFeeder = new BoltFeederUnit(FasteningHead.Shooting, io, new());
        var layout = new PcbLayout

        {

            BoltPoints = [
                Bolt(1, FasteningHead.Pickup, 20, 30),
                Bolt(2, FasteningHead.Shooting, 20, 30),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 30, Y = 40 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 30, Y = 40 },
            ],
        };
        var station = new BoltFasteningStation(shootingHead,
            pickupHead,
            io,
            motion,
            settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new AxisPosition { X = 0, Y = 0 },
                LowerRightLocatingPin = new AxisPosition { X = 100, Y = 100 },
            },
            work,
            pickupFeeder,
            shootingFeeder,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        var movedWithLoweredCylinder = false;
        var movedBelowTravelZ = false;
        var fasteningHeights = new List<(byte Head, double Z)>();
        var runningHeads = new HashSet<byte>();
        var feedingHeads = new List<byte>();
        motion.PositionChanged += (_, _, _) =>
            movedWithLoweredCylinder |= motion.IsMovingHorizontal
                && !gantry.IsHorizontalMoveAllowed;
        bus.FrameTransferred += (direction, frame) =>
        {
            if (direction == AdcFrameDirection.Transmit
                && frame[1] == (byte)AdcFunctionCode.WriteSingleRegister
                && BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)) == (ushort)AdcRemoteRegister.RemoteStart)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)) != 0)
                {
                    Assert.True(gantry.IsHorizontalMoveAllowed); // START precedes descent.
                    runningHeads.Add(frame[0]);
                    fasteningHeights.Add((frame[0], motion.GetPosition().Z));
                }
                else
                    runningHeads.Remove(frame[0]);
            }
        };
        io.OutputChanged += (output, on) =>
        {
            switch (true)
            {
                case true when !on || output is not (OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown):
                    return;
                case true when output == OutputIo.PickupHeadDown && motion.GetPosition().X == settings.PickupPosition.X:
                    return; // Bolt pickup uses its own cylinder sequence.
            }
            var address = (byte)(output == OutputIo.PickupHeadDown ? 1 : 2);
            Assert.Contains(address, runningHeads);
            feedingHeads.Add(address);
        };

        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToSafeZAsync();
        motion.PositionChanged += (_, _, z) =>
            movedBelowTravelZ |= motion.IsMovingHorizontal
                && Math.Abs(z - settings.SafeZ) > MotionService.PositionToleranceMillimeters;
        await gantry.CheckReadyAsync();
        bus.SetNextFasteningResult(2, AdcEventStatus.FasteningNg);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        Assert.True(work.Station.CarrierSeated);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);

        using var feederCancellation = new CancellationTokenSource();
        var feederRuns = Task.WhenAll(
            pickupFeeder.RunAsync(feederCancellation.Token),
            shootingFeeder.RunAsync(feederCancellation.Token));
        try
        {
            using (var stopDuringApproach = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                settings.Motion.ZSpeed = 20;
                void StopDuringFasteningApproach(double x, double y, double z)
                {
                    if (x == 20 && y == 30 && z > settings.SafeZ + 0.05)
                        stopDuringApproach.Cancel();
                }
                motion.PositionChanged += StopDuringFasteningApproach;
                try
                {
                    await station.RunAsync(stopDuringApproach.Token);
                }
                finally
                {
                    motion.PositionChanged -= StopDuringFasteningApproach;
                    settings.Motion.ZSpeed = 20_000;
                }
                Assert.InRange(
                    motion.GetPosition().Z,
                    settings.SafeZ + 0.05,
                    settings.ShootingHead.FasteningZ - 0.05);
                Assert.Empty(tightenings);
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.False(work.Completed);
            }

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
                    await station.RunAsync(stopAfterPickup.Token);
                }
                finally
                {
                    io.InputChanged -= StopWithPickedBolt;
                }

                Assert.True(
                    gantry.PickupBoltLoaded,
                    $"State={station.GetState()}, Seated={work.Station.CarrierSeated}, Position={motion.GetPosition()}");
                Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
                Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
                Assert.Equal(settings.PickupPosition.Z, motion.GetPosition().Z);
                Assert.DoesNotContain(tightenings, item => item.Head == 1);
                Assert.False(work.Completed);
            }

            using var cancellation = new CancellationTokenSource();
            var resumedRun = station.RunAsync(cancellation.Token);
            try
            {
                Assert.True(
                    await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10)),
                    $"State={station.GetState()}, Error={resumedRun.Exception?.GetBaseException().Message}");
            }
            finally
            {
                cancellation.Cancel();
                await resumedRun;
            }

            var heatSink1 = work.GetAssembly(HeatSinkSlot.HeatSink1);
            var heatSink2 = work.GetAssembly(HeatSinkSlot.HeatSink2);
            Assert.Equal(AssemblyResult.Ng, heatSink1.FasteningResult);
            Assert.Equal(AssemblyResult.Ok, heatSink2.FasteningResult);
            Assert.False(heatSink1.PcbBoltResults[2].Success);
            Assert.True(heatSink1.PickupBoltResults[1].Success);
            Assert.True(heatSink2.PcbBoltResults[2].Success);
            Assert.True(heatSink2.PickupBoltResults[1].Success);
            Assert.False(pickupHead.HasPendingResult);
            Assert.False(shootingHead.HasPendingResult);
            Assert.False(movedWithLoweredCylinder);
            Assert.False(movedBelowTravelZ);
            Assert.Equal(4, fasteningHeights.Count);
            Assert.Equal(new byte[] { 2, 2, 1, 1 }, feedingHeads);
            Assert.All(fasteningHeights, item => Assert.Equal(
                item.Head == 1 ? settings.PickupHead.FasteningZ : settings.ShootingHead.FasteningZ,
                item.Z));
            Assert.True(gantry.IsHorizontalMoveAllowed);
            Assert.True(motion.IsAtHorizontalZ);
            Assert.Equal(
                new (byte Head, ushort Preset)[] { (2, 1), (2, 1), (1, 1), (1, 1) },
                tightenings);
            // A replaced carrier must never inherit the previous carrier's in-flight result.
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
            VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            using var carrierChange = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            afterStop = () =>
            {
                VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
                VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
                carrierChange.Cancel();
            };
            var changedCarrier = await Assert.ThrowsAsync<InvalidOperationException>(
                () => station.RunAsync(carrierChange.Token));
            Assert.Contains("Carrier work changed", changedCarrier.Message);
            Assert.Empty(previousAssembly.PcbBoltResults);
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
    public async Task FeederWaitRechecksSupplyFeedbackBeforeNextOperation(FasteningHead head)
    {
        var settings = new BoltFasteningSettings
        {
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 10, Y = 10, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
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

        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, head, 10, 10)],
        };
        var feederSettings = new BoltFeederSettings();
        var station = new BoltFasteningStation(new AdcBoltHead(bus, new HantasSettings(), 2, "Virtual", 115200),
            new AdcBoltHead(bus, new HantasSettings(), 1, "Virtual", 115200),
            io,
            motion,
            settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100, Y = 100 }, },
            new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new()),
            new BoltFeederUnit(FasteningHead.Pickup, io, feederSettings),
            new BoltFeederUnit(FasteningHead.Shooting, io, feederSettings),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new());
        var gantry = station;
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToXYAsync(10, 10);
        if (head == FasteningHead.Pickup)
        {
            io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        }
        else
        {
            await gantry.MoveZAsync(settings.ShootingHead.FasteningZ);
        }

        VirtualTest.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        Assert.Equal(
            head == FasteningHead.Pickup
                ? BoltFasteningState.WaitingForPickupFeeder
                : BoltFasteningState.WaitingForShootingFeeder,
            station.GetState());

        using (var cancelled = new CancellationTokenSource())
        {
            var waiting = station.RunAsync(cancelled.Token);
            Assert.False(waiting.IsCompleted);
            if (head == FasteningHead.Pickup)
            {
                Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.False(io.GetOutput(OutputIo.PickupHeadDown));
                Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            }
            cancelled.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }

        if (head == FasteningHead.Pickup)
            await gantry.MoveToXYAsync(0, 0);
        var stationChanges = 0;
        var gantryChanges = 0;
        station.Changed += () => Interlocked.Increment(ref stationChanges);
        gantry.Changed += () => Interlocked.Increment(ref gantryChanges);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var continued = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.ShootingEscapeForward)
            {
                if (value)
                    Assert.True(io.GetInput(InputIo.ShootingFeederBoltDetected));
                io.SetInputs((InputIo.ShootingEscapeForward, value), (InputIo.ShootingEscapeBackward, !value));
            }
            if (head == FasteningHead.Pickup && output == OutputIo.PickupHeadDown && value)
            {
                Assert.True(io.GetInput(InputIo.PickupFeederBoltDetected));
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
            }
            if (head == FasteningHead.Pickup
                ? output == OutputIo.PickupHeadVacuumPump && value
                : output == OutputIo.ShootBolt && value)
            {
                continued = true;
                stop.Cancel();
            }
        };
        var run = station.RunAsync(stop.Token);
        try
        {
            if (head == FasteningHead.Pickup)
            {
                Assert.True(await WaitUntilAsync(
                    () => station.GetState() == BoltFasteningState.WaitingForPickupFeeder,
                    TimeSpan.FromSeconds(1)));
                Assert.Equal(BoltCylinderState.Up, station.PickupHeadPosition);
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.False(io.GetOutput(OutputIo.PickupHeadDown));
                Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
                io.SetInput(InputIo.PickupFeederBoltDetected, true);
            }
            else
            {
                io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
                io.SetInput(InputIo.ShootingEscapeBackward, false);
                io.SetInput(InputIo.ShootingEscapeForward, true);
                io.SetInput(InputIo.ShootingTubeBoltDetected, true);
                await Task.Delay(50);
                Assert.False(continued);
                Assert.False(run.IsCompleted);
                Assert.False(io.GetOutput(OutputIo.ShootBolt));
                io.SetInput(InputIo.ShootingTubeBoltDetected, false);
                io.SetInput(InputIo.ShootingEscapeForward, false);
                io.SetInput(InputIo.ShootingEscapeBackward, true);
                io.SetInput(InputIo.ShootingFeederBoltDetected, true);
            }

            Assert.True(await WaitUntilAsync(() => continued, TimeSpan.FromSeconds(1)));
        }
        finally
        {
            stop.Cancel();
            await run;
        }

        Assert.Equal(head == FasteningHead.Pickup, io.GetInput(InputIo.PickupFeederBoltDetected));
        Assert.Equal(head == FasteningHead.Shooting, io.GetInput(InputIo.ShootingFeederBoltDetected));
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(stationChanges > 0);
        Assert.True(gantryChanges > 0); // Display listeners still receive head feedback.
        Assert.Equal(
            head == FasteningHead.Pickup ? settings.PickupPosition.Z : settings.ShootingHead.FasteningZ,
            motion.GetPosition().Z);
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    [InlineData(FasteningHead.Pickup, true)]
    [InlineData(FasteningHead.Shooting, false, true)]
    [InlineData(FasteningHead.Pickup, false, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, false, true)]
    public async Task TeachingBoltMoveWaitsForTableAtSafeZBeforeXyAndFasteningZ(
        FasteningHead head,
        bool stopAtTable = false,
        bool conflictingTableFeedback = false,
        bool loseTableDuringXy = false,
        bool loseTableDuringFasteningZ = false)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = loseTableDuringFasteningZ ? 50 : 20_000 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.PickupHead.FasteningZ = 16;
        settings.ShootingHead.FasteningZ = 12;
        var reference = new CarrierReferenceSettings
        {
            UpperLeftLocatingPin = new(),
            LowerRightLocatingPin = new() { X = 100, Y = 100 },
        };
        var bolt = Bolt(1, head, 20, 30);
        var layout = new PcbLayout { BoltPoints = [bolt] };
        var point = settings.GetTeachingPositions(layout, bolt.HeatSink, reference)
            .Single(point => point.Bolt == bolt);
        var destination = point.Read();
        Assert.Equal(TeachMode.Full, point.Mode);
        var tableDown = head == FasteningHead.Pickup;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()),
            new() { TimeoutMilliseconds = conflictingTableFeedback ? 250 : 2_000 })
        { AutoResponseEnabled = false };
        io.SetOutput(OutputIo.PickupTableDown, !tableDown);
        io.SetInputs(
            (InputIo.PickupTableUp, tableDown), (InputIo.PickupTableDown, !tableDown),
            (InputIo.PickupHeadUp, true), (InputIo.PickupHeadDown, false),
            (InputIo.ShootingHeadUp, true), (InputIo.ShootingHeadDown, false));
        using var motion = new VirtualMotionService(settings.Motion, new(), horizontalZ: () => settings.SafeZ);
        var bus = new VirtualAdcBus();
        var station = CreateFastening(
            new AdcBoltHead(bus, new(), 2, "Virtual", 115200), new AdcBoltHead(bus, new(), 1, "Virtual", 115200), io, motion, settings, reference);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await motion.MoveAxisAsync(MotionAxis.Z, 9, 20_000);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var tableCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new List<string>();
        motion.MovingChanged += moving =>
        {
            if (!moving)
                return;
            steps.Add(motion.IsMovingHorizontal ? "XY" : "Z");
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                Assert.Equal(tableDown ? BoltCylinderState.Down : BoltCylinderState.Up, station.PickupTablePosition);
            }
        };
        motion.PositionChanged += (x, y, z) =>
        {
            if (loseTableDuringXy && motion.IsMovingHorizontal)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
            if (loseTableDuringFasteningZ && !motion.IsMovingHorizontal
                && x == destination.X && y == destination.Y && z > settings.SafeZ + 0.05)
                io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, false));
        };
        io.OutputChanged += (output, on) =>
        {
            Assert.Equal(OutputIo.PickupTableDown, output);
            Assert.Equal(tableDown, on);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
            steps.Add("Table");
            tableCommand.TrySetResult();
        };

        var move = station.MoveToTeachingPositionAsync(point, destination, stop.Token);
        await tableCommand.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(move.IsCompleted);
        Assert.Equal(new[] { "Z", "Table" }, steps);
        Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        if (stopAtTable)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        }
        else if (conflictingTableFeedback)
        {
            io.SetInputs((InputIo.PickupTableUp, true), (InputIo.PickupTableDown, true));
            await Assert.ThrowsAsync<IoTimeoutException>(() => move);
            Assert.Equal((0, 0, settings.SafeZ), motion.GetPosition());
        }
        else
        {
            io.SetInputs((InputIo.PickupTableUp, !tableDown), (InputIo.PickupTableDown, tableDown));
            if (loseTableDuringXy || loseTableDuringFasteningZ)
            {
                await Assert.ThrowsAsync<MotionInterlockException>(() => move);
                if (loseTableDuringFasteningZ)
                {
                    Assert.Equal(destination.X, motion.GetPosition().X);
                    Assert.Equal(destination.Y, motion.GetPosition().Y);
                    Assert.InRange(motion.GetPosition().Z, settings.SafeZ, destination.Z - 0.05);
                }
                else
                {
                    Assert.InRange(motion.GetPosition().X, 0, destination.X - 0.05);
                    Assert.InRange(motion.GetPosition().Y, 0, destination.Y - 0.05);
                    Assert.Equal(settings.SafeZ, motion.GetPosition().Z);
                }
            }
            else
            {
                await move;
                Assert.Equal(new[] { "Z", "Table", "XY", "Z" }, steps);
                Assert.Equal((destination.X, destination.Y, destination.Z), motion.GetPosition());
            }
        }
        Assert.Equal((destination.X, destination.Y, destination.Z), (point.Read().X, point.Read().Y, point.Read().Z));
        Assert.True(station.IsHorizontalMoveAllowed);
        Assert.False(motion.IsMoving);
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
            LowerRightLocatingPin = new AxisPosition { X = 100, Y = 100 },
        };
    }

    private sealed class AdcResponseStream : MemoryStream
    {
        private readonly int _pauseAtByte;
        private readonly int _delayMilliseconds;
        private readonly TaskCompletionSource<int> _pending;

        public AdcResponseStream(
            byte[] bytes,
            int pauseAtByte = -1,
            int delayMilliseconds = 0)
            : base(bytes)
        {
            _pauseAtByte = pauseAtByte;
            _delayMilliseconds = delayMilliseconds;
            _pending = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Waiting = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource Waiting { get; }
        public bool Aborted { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position == _pauseAtByte)
            {
                await Task.Delay(_delayMilliseconds, cancellationToken);
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

    public enum ShootingPreparationFailure
    {
        None,
        Motion,
        Supply,
        Stop,
    }

    public enum AdcResponseKind
    {
        Valid,
        InvalidCrc,
        WrongAddress,
        WriteResponse,
        ControllerError,
    }
}
