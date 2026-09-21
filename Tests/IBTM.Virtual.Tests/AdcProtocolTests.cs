using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AdcProtocolTests
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
    public async Task SeparateAdcPortsKeepSameAddressResultsAndDisconnectionsIndependent()
    {
        var settings = new MachineSettings
        {
            Hantas = new()
            {
                PickupPortName = "COM4", PickupBaudRate = 19200, PickupSlaveAddress = 1,
                ShootingPortName = "COM5", ShootingBaudRate = 38400, ShootingSlaveAddress = 1,
            },
        };
        await using var services = new ServiceCollection()
            .AddSingleton(VirtualTest.OpenMachineStore())
            .AddIbtmApplication(settings)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        var pickupBus = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup);
        var shootingBus = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting);
        var pickup = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup);
        var shooting = services.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting);
        Assert.NotSame(pickupBus, shootingBus);
        Assert.Null(services.GetService<IAdcBus>());

        await Task.WhenAll(pickup.CheckReadyAsync(), shooting.CheckReadyAsync());
        Assert.Equal("COM4", pickupBus.PortName);
        Assert.Equal(19200, pickupBus.BaudRate);
        Assert.Equal("COM5", shootingBus.PortName);
        Assert.Equal(38400, shootingBus.BaudRate);
        await Task.WhenAll(pickup.SelectPresetAsync(2), shooting.SelectPresetAsync(3));
        ((VirtualAdcBus)pickupBus).SetNextFasteningResult(1, AdcEventStatus.FasteningNg);
        var results = await Task.WhenAll(pickup.TightenAsync(), shooting.TightenAsync());
        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal((ushort)2, (await pickupBus.ReadFasteningResultAsync(1)).Preset);
        Assert.Equal((ushort)3, (await shootingBus.ReadFasteningResultAsync(1)).Preset);

        pickupBus.Close();
        Assert.False(pickupBus.IsOpen);
        Assert.True(shootingBus.IsOpen);
        await shooting.CheckReadyAsync();
        Assert.Equal("COM5", shootingBus.PortName);
    }

    [Fact]
    public void SeparateAdcSettingsPreserveTheExistingPortOnlyForPickup()
    {
        var settings = JsonSerializer.Deserialize<HantasSettings>(
            """{"PortName":"COM4","BaudRate":19200,"PickupSlaveAddress":2,"ShootingSlaveAddress":3}""")!;
        Assert.Equal("COM4", settings.PickupPortName);
        Assert.Equal(19200, settings.PickupBaudRate);
        Assert.Empty(settings.ShootingPortName);
        Assert.Equal((byte)2, settings.PickupSlaveAddress);
        Assert.Equal((byte)3, settings.ShootingSlaveAddress);
        settings.ShootingPortName = "COM5";
        settings.ShootingBaudRate = 38400;
        settings.ShootingSlaveAddress = 2;

        var reloaded = JsonSerializer.Deserialize<HantasSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal("COM4", reloaded.PickupPortName);
        Assert.Equal(19200, reloaded.PickupBaudRate);
        Assert.Equal("COM5", reloaded.ShootingPortName);
        Assert.Equal(38400, reloaded.ShootingBaudRate);
        Assert.Equal(reloaded.PickupSlaveAddress, reloaded.ShootingSlaveAddress);
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
            AdcResponseKind.ControllerError => [0x03],
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
        {
            var error = await Assert.ThrowsAsync<AdcResponseException>(() => reading);
            Assert.Equal(0x03, error.ErrorCode);
            Assert.Contains("InvalidDataLength", error.Message);
            Assert.Contains(Convert.ToHexString(frame), error.Message);
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcAutomaticResultCanArriveBeforeOrAfterStartEcho(bool resultFirst)
    {
        var data = new byte[29];
        data[0] = 28;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), 7);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(25), (ushort)AdcEventStatus.FasteningOk);
        var automatic = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
        var echo = AdcRtuFrame.Build(1, AdcFunctionCode.WriteSingleRegister, [0x0F, 0xA3, 0, 1]);
        byte[] incoming = resultFirst ? [.. automatic, .. echo] : [.. echo, .. automatic];
        using var stream = new AdcResponseStream(incoming);
        var queued = new Queue<byte[]>();
        var chunks = new List<byte[]>();
        var response = await AdcBus.ReadResponseAsync(stream, stream.Abort, chunks.Add,
            1, AdcFunctionCode.WriteSingleRegister, CancellationToken.None, resultReceived: queued.Enqueue);
        Assert.Equal(echo, response);

        var received = queued.TryDequeue(out var early)
            ? early
            : await AdcBus.ReadResponseAsync(stream, stream.Abort, chunks.Add,
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, expectedByteCount: 28);

        Assert.Equal(automatic, received);
        Assert.Empty(queued);
        Assert.Equal(incoming, chunks.SelectMany(chunk => chunk).ToArray());
    }

    [Fact]
    public async Task AdcAutomaticEventIsNotMistakenForRunFeedback()
    {
        var data = new byte[29];
        data[0] = 28;
        var automatic = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
        var statusData = new byte[15];
        statusData[0] = 14;
        var status = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, statusData);
        using var stream = new AdcResponseStream([.. automatic, .. status]);
        var events = new List<byte[]>();

        var response = await AdcBus.ReadResponseAsync(stream, stream.Abort, bytes => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14, events.Add);

        Assert.Equal(status, response);
        Assert.Equal(automatic, Assert.Single(events));
    }

    [Theory]
    [InlineData(1)] // Every byte arrives separately.
    [InlineData(256)] // Several frames are already available in the receive buffer.
    public async Task AdcStopRejectionBetweenAutomaticEventsKeepsFrameBoundaries(int chunkSize)
    {
        var data = new byte[29];
        data[0] = 28;
        // Header-like bytes inside a payload must not restart frame collection.
        data[5] = 0x01;
        data[6] = 0x86;
        data[7] = 0x03;
        var firstEvent = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), 2);
        var nextEvent = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
        byte[] rejection = [0x01, 0x86, 0x03, 0x02, 0x61];
        byte[] incoming = [.. firstEvent, .. rejection, .. nextEvent];
        using var stream = new AdcResponseStream(incoming, chunkSize: chunkSize);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var events = new List<byte[]>();
        var chunks = new List<byte[]>();

        var error = await Assert.ThrowsAsync<AdcResponseException>(() => AdcBus.ReadResponseAsync(
            stream, stream.Abort, chunks.Add, 1, AdcFunctionCode.WriteSingleRegister,
            timeout.Token, resultReceived: events.Add));

        Assert.Equal(0x03, error.ErrorCode);
        Assert.Contains("RX=0186030261", error.Message);
        Assert.Equal(firstEvent, Assert.Single(events));
        Assert.Equal(firstEvent.Length + rejection.Length, stream.Position);

        var remaining = await AdcBus.ReadResponseAsync(
            stream, stream.Abort, chunks.Add, 1, AdcFunctionCode.ReadInputRegisters,
            timeout.Token, expectedByteCount: 28);

        Assert.Equal(nextEvent, remaining);
        Assert.Equal(incoming, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.False(stream.Aborted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcAutomaticEventValidatesAddressAndCrcBeforeQueuing(bool wrongAddress)
    {
        var data = new byte[29];
        data[0] = 28;
        var automatic = AdcRtuFrame.Build((byte)(wrongAddress ? 2 : 1), AdcFunctionCode.ReadInputRegisters, data);
        if (!wrongAddress)
            automatic[^1] ^= 0xFF;
        using var stream = new AdcResponseStream(automatic);
        var events = new List<byte[]>();

        await Assert.ThrowsAsync<InvalidDataException>(() => AdcBus.ReadResponseAsync(
            stream, stream.Abort, bytes => { }, 1, AdcFunctionCode.WriteSingleRegister,
            CancellationToken.None, resultReceived: events.Add));

        Assert.Empty(events);
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

    private sealed class AdcResponseStream : MemoryStream
    {
        private readonly int _pauseAtByte;
        private readonly int _delayMilliseconds;
        private readonly int _chunkSize;
        private readonly TaskCompletionSource<int> _pending;

        public AdcResponseStream(
            byte[] bytes,
            int pauseAtByte = -1,
            int delayMilliseconds = 0,
            int chunkSize = 1)
            : base(bytes)
        {
            _pauseAtByte = pauseAtByte;
            _delayMilliseconds = delayMilliseconds;
            _chunkSize = chunkSize;
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
                return await base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
            Waiting.TrySetResult();
            return await _pending.Task; // Model Windows native IO ignoring cancellation.
        }

        public void Abort()
        {
            Aborted = true;
            _pending.TrySetException(new IOException("Native serial IO aborted."));
        }
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
