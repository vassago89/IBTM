using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
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
        Assert.Equal(100, settings.StatusPollMilliseconds);
        settings.ShootingPortName = "COM5";
        settings.ShootingBaudRate = 38400;
        settings.ShootingSlaveAddress = 2;
        settings.StatusPollMilliseconds = 75;

        var reloaded = JsonSerializer.Deserialize<HantasSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal("COM4", reloaded.PickupPortName);
        Assert.Equal(19200, reloaded.PickupBaudRate);
        Assert.Equal("COM5", reloaded.ShootingPortName);
        Assert.Equal(38400, reloaded.ShootingBaudRate);
        Assert.Equal(reloaded.PickupSlaveAddress, reloaded.ShootingSlaveAddress);
        Assert.Equal(75, reloaded.StatusPollMilliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.StatusPollMilliseconds = 0);
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
    public async Task RequestReadLogsEachChunkBeforeFrameValidation(AdcResponseKind kind)
    {
        var function = kind switch
        {
            AdcResponseKind.ControllerError => (AdcFunctionCode)0x84,
            AdcResponseKind.WriteResponse => AdcFunctionCode.WriteSingleRegister,
            _ => AdcFunctionCode.ReadInputRegisters,
        };
        byte[] data = kind switch
        {
            AdcResponseKind.ControllerError => [0x03],
            AdcResponseKind.WriteResponse => [0x0F, 0xA3, 0x00, 0x00],
            _ => [0x02, 0x12, 0x34],
        };
        var frame = AdcRtuFrame.Build((byte)(kind == AdcResponseKind.WrongAddress ? 1 : 0), function, data);
        if (kind == AdcResponseKind.InvalidCrc)
            frame[^1] ^= 0xFF;
        using var stream = new ReplyStream { MaximumRead = 1 };
        var chunks = new List<byte[]>();
        stream.Feed(frame);
        var waiting = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, chunks.Add,
            0, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 2);

        if (kind == AdcResponseKind.Valid)
            Assert.Equal(frame, await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        else if (kind == AdcResponseKind.ControllerError)
        {
            var error = await Assert.ThrowsAsync<AdcResponseException>(() => waiting);
            Assert.Equal(3, error.ErrorCode);
            Assert.Contains("InvalidDataLength", error.Message);
            Assert.Contains(Convert.ToHexString(frame), error.Message);
        }
        else if (kind == AdcResponseKind.WriteResponse)
        {
            var error = await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() => waiting);
            Assert.Contains("Write Single Register", error.Message);
            Assert.Contains("kind=normal", error.Message);
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => waiting);
        Assert.Equal(frame, chunks.SelectMany(chunk => chunk).ToArray());
        Assert.All(chunks, chunk => Assert.Single(chunk));
    }

    [Fact]
    public async Task Logged8C03ReplyIsInterpretedAsAModbusExceptionForADifferentFunction()
    {
        using var stream = new ReplyStream();
        // Exact equipment reply: CRC is valid, but the expected exception function was 0x84.
        byte[] frame = [0x01, 0x8C, 0x03, 0x04, 0xC1];
        stream.Feed(frame[..2]);
        var waiting = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 28);
        Assert.False(waiting.IsCompleted);
        stream.Feed(frame[2..]);
        var error = await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() => waiting);
        Assert.Contains("RX=018C0304C1", error.Message);
        Assert.Contains("function=0x8C", error.Message);
        Assert.Contains("base function=0x0C (Get Comm Event Log)", error.Message);
        Assert.Contains("exception=0x03 (Illegal Data Value)", error.Message);
        Assert.Contains("expected response=0x84", error.Message);
        Assert.Contains("ADC firmware meaning unconfirmed", error.Message);
        Assert.DoesNotContain("InvalidDataLength", error.Message);
    }

    [Theory]
    [InlineData(AdcFunctionCode.ReadInputRegisters, 14, 0x8C, 0x03)]
    [InlineData(AdcFunctionCode.ReadInputRegisters, 28, 0x8C, 0x04)]
    [InlineData(AdcFunctionCode.ReadInputRegisters, 28, 0x86, 0x03)]
    [InlineData(AdcFunctionCode.WriteSingleRegister, 28, 0x8C, 0x03)]
    public void ValidRtuExceptionsAreInterpretedRegardlessOfThePendingRequest(
        AdcFunctionCode request, int byteCount, byte function, byte data)
    {
        var frame = AdcRtuFrame.Build(1, (AdcFunctionCode)function, [data]);
        var error = Assert.Throws<AdcUnexpectedResponseException>(
            () => AdcBus.ValidateResponse(frame, 1, request, byteCount));
        Assert.Contains($"function=0x{function:X2}", error.Message);
        Assert.Contains($"exception=0x{data:X2}", error.Message);
        Assert.Contains("CRC valid", error.Message);
    }

    [Theory]
    [InlineData(0x83, "06", "Read Holding Registers", "Server Device Busy")]
    [InlineData(0x91, "04", "Report Server ID", "Server Device Failure")]
    [InlineData(0xD1, "55", "Vendor-specific / unspecified function", "unspecified exception")]
    [InlineData(0x03, "021234", "Read Holding Registers", "data=021234")]
    [InlineData(0x05, "0001FF00", "Write Single Coil", "data=0001FF00")]
    [InlineData(0x07, "01", "Read Exception Status", "data=01")]
    [InlineData(0x0B, "00000001", "Get Comm Event Counter", "data=00000001")]
    [InlineData(0x16, "0001FFFF0000", "Mask Write Register", "data=0001FFFF0000")]
    [InlineData(0x18, "000400011234", "Read FIFO Queue", "data=000400011234")]
    [InlineData(0x2B, "0E0101000001000141", "Encapsulated Interface Transport", "data=0E0101000001000141")]
    public async Task DifferentRtuResponseShapesAreReassembledAndInterpreted(
        byte function, string data, string functionName, string interpretedData)
    {
        using var stream = new ReplyStream { MaximumRead = 1 };
        var frame = AdcRtuFrame.Build(1, (AdcFunctionCode)function, Convert.FromHexString(data));
        var result = ResultFrame(31);
        stream.Feed([.. frame, .. result]);
        var error = await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() =>
            AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 28));
        Assert.Contains(functionName, error.Message);
        Assert.Contains(interpretedData, error.Message);
        Assert.Contains($"RX={Convert.ToHexString(frame)}", error.Message);
        // Read only the first frame, leaving the following frame intact.
        Assert.Equal(result, await AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 28));
    }

    [Fact]
    public void ValidCrcDoesNotMakeAnInvalidRtuShapeAcceptable()
    {
        // The byte count claims eight bytes, but only two follow it.
        var frame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadHoldingRegisters, [8, 0x12, 0x34]);
        Assert.Throws<InvalidDataException>(() => AdcBus.ValidateResponse(
            frame, 1, AdcFunctionCode.ReadInputRegisters, 28));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonstandardExceptionWithBadCrcOrAddressRemainsACommunicationFailure(bool wrongAddress)
    {
        using var stream = new ReplyStream();
        byte[] frame = [0x01, 0x8C, 0x03, 0x04, 0xC1];
        if (wrongAddress)
        {
            frame[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), AdcRtuFrame.CalculateCrc(frame.AsSpan(0, 3)));
        }
        else
            frame[^1] ^= 0xFF;
        stream.Feed(frame);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 28));
        Assert.Contains(wrongAddress ? "address=2" : "CRC is invalid", error.Message);
        Assert.Contains($"RX={Convert.ToHexString(frame)}", error.Message);
        Assert.Contains(wrongAddress ? "CRC valid" : "calculated=", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task CancelledReadKeepsLoggedBytesAndNextRequestUsesANewBuffer(int receivedCount)
    {
        var oldFrame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, [2, 0x12, 0x34]);
        var nextFrame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, [2, 0x56, 0x78]);
        using var stream = new ReplyStream();
        using var cancellation = new CancellationTokenSource();
        var logged = new List<byte[]>();
        if (receivedCount > 0)
            stream.Feed(oldFrame[..receivedCount]);
        var waiting = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, logged.Add,
            1, AdcFunctionCode.ReadInputRegisters, cancellation.Token, 2);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(oldFrame[..receivedCount], logged.SelectMany(chunk => chunk).ToArray());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // The exchange discards bytes from an expired request before sending again.
        stream.Feed(oldFrame[receivedCount..]);
        stream.DiscardInput();
        stream.Feed(nextFrame);
        Assert.Equal(nextFrame, await AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 2));
    }

    [Fact]
    public async Task LoggedStatusReadRejectionRemainsAnError()
    {
        using var stream = new ReplyStream();
        stream.Feed([1, 0x84, 3, 3, 1]);
        var error = await Assert.ThrowsAsync<AdcResponseException>(() =>
            AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14));
        Assert.Equal(3, error.ErrorCode);
    }

    [Fact]
    public async Task StatusAndResultRepliesAreReadSeparatelyAndLengthMismatchIsRejected()
    {
        using var stream = new ReplyStream();
        byte[] statusData = [14, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        var status = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, statusData);
        var result = ResultFrame(10);
        stream.Feed([.. status, .. result]);
        Assert.Equal(status, await AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14));
        Assert.Equal(result, await AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 28));
        stream.Feed(result);
        await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() =>
            AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14));
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeWaitingForItsPayload()
    {
        using var stream = new ReplyStream();
        stream.Feed([1, 4, 255]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
                1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14));
    }

    [Fact]
    public async Task RawCaptureKeepsEchoAndLateBytesUntilDeadline()
    {
        using var stream = new ReplyStream();
        byte[] echo = [0, 0x11, 0xC1, 0xBC];
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [2, 1, 0xFF]);
        stream.Feed(echo);
        var capture = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            0, AdcFunctionCode.RequestDeviceInformation, CancellationToken.None, captureMilliseconds: 100);
        await Task.Delay(10);
        Assert.False(capture.IsCompleted);
        stream.Feed([.. frame, 0xAB]);
        Assert.Equal([.. echo, .. frame, 0xAB], await capture);
    }

    [Fact]
    public async Task RawCaptureCancellationLeavesTheStreamAvailableForTheNextResponse()
    {
        using var stream = new ReplyStream();
        using var cancellation = new CancellationTokenSource();
        byte[] echo = [0, 0x11, 0xC1, 0xBC];
        stream.Feed(echo);
        var chunks = new List<byte[]>();
        var capture = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, chunks.Add,
            0, AdcFunctionCode.RequestDeviceInformation, cancellation.Token, captureMilliseconds: 3000);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(echo, chunks.SelectMany(chunk => chunk).ToArray());
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [2, 1, 0xFF]);
        stream.Feed(frame);
        Assert.Equal(frame, await AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            0, AdcFunctionCode.RequestDeviceInformation, CancellationToken.None));
    }

    [Fact]
    public async Task ClosingTheStreamReleasesAPendingResponse()
    {
        using var stream = new ReplyStream();
        var waiting = AdcBus.ReadResponseAsync(stream, stream.DiscardInput, _ => { },
            1, AdcFunctionCode.ReadInputRegisters, CancellationToken.None, 14);
        stream.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => waiting);
    }

    private static byte[] ResultFrame(ushort eventCount)
    {
        var data = new byte[29];
        data[0] = 28;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), eventCount);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(25), (ushort)AdcEventStatus.FasteningOk);
        return AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
    }

    // Test serial input with independently arriving fragments; never opens hardware.
    private sealed class ReplyStream : Stream
    {
        private readonly Channel<byte[]> _chunks;
        private ReadOnlyMemory<byte> _remaining;

        public ReplyStream()
        {
            _chunks = Channel.CreateUnbounded<byte[]>();
        }

        public int MaximumRead { get; init; } = 256;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Feed(byte[] bytes)
        {
            _chunks.Writer.TryWrite(bytes);
        }

        public void DiscardInput()
        {
            _remaining = default;
            while (_chunks.Reader.TryRead(out _)) { }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining.IsEmpty)
            {
                try
                {
                    _remaining = await _chunks.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }
            }
            var count = Math.Min(MaximumRead, Math.Min(buffer.Length, _remaining.Length));
            _remaining[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Flush() { throw new NotSupportedException(); }
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
