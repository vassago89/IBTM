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
    public async Task EventReceiveLogsEachChunkBeforeFrameValidation(AdcResponseKind kind)
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
        using var bus = new AdcBus(new());
        var chunks = new List<byte[]>();
        bus.FrameTransferred += (direction, bytes) =>
        {
            Assert.Equal(AdcFrameDirection.Receive, direction);
            chunks.Add(bytes);
        };
        var pending = bus.BeginResponse(0, AdcFunctionCode.ReadInputRegisters, 2);
        var waiting = bus.WaitForResponseAsync(pending, CancellationToken.None);
        foreach (var value in frame)
            bus.ReceiveBytes([value]);

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
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        // Exact equipment reply: CRC is valid, but the expected exception function was 0x84.
        byte[] frame = [0x01, 0x8C, 0x03, 0x04, 0xC1];
        bus.ReceiveBytes(frame[..2]);
        Assert.False(pending.Completion.Task.IsCompleted);
        bus.ReceiveBytes(frame[2..]);
        var error = await Assert.ThrowsAsync<AdcUnexpectedResponseException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
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
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        var frame = AdcRtuFrame.Build(1, (AdcFunctionCode)function, Convert.FromHexString(data));
        foreach (var value in frame)
            bus.ReceiveBytes([value]);
        var error = await Assert.ThrowsAsync<AdcUnexpectedResponseException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Contains(functionName, error.Message);
        Assert.Contains(interpretedData, error.Message);
        Assert.Contains($"RX={Convert.ToHexString(frame)}", error.Message);
        // The next normal reply keeps its boundary and request ownership.
        var next = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        var result = AutomaticFrame(31);
        bus.ReceiveBytes(result);
        Assert.Equal(result, await bus.WaitForResponseAsync(next, CancellationToken.None));
    }

    [Fact]
    public void ValidCrcDoesNotMakeAnInvalidRtuShapeAcceptable()
    {
        // The byte count claims eight bytes, but only two follow it.
        var frame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadHoldingRegisters, [8, 0x12, 0x34]);
        Assert.Throws<InvalidDataException>(() => AdcBus.ValidateResponse(
            frame, 1, AdcFunctionCode.ReadInputRegisters, 28));
    }

    [Fact]
    public async Task DifferentFunctionAndResultInOneChunkKeepTheirBoundaries()
    {
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        var unrelated = AdcRtuFrame.Build(1, (AdcFunctionCode)0x0B, [0, 0, 0, 1]);
        var result = AutomaticFrame(32);
        bus.ReceiveBytes([.. unrelated, .. result]);
        await Assert.ThrowsAsync<AdcUnexpectedResponseException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Equal((ushort)32, (await bus.WaitForFasteningResultAsync(1, CancellationToken.None)).EventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonstandardExceptionWithBadCrcOrAddressRemainsACommunicationFailure(bool wrongAddress)
    {
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        byte[] frame = [0x01, 0x8C, 0x03, 0x04, 0xC1];
        if (wrongAddress)
        {
            frame[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), AdcRtuFrame.CalculateCrc(frame.AsSpan(0, 3)));
        }
        else
            frame[^1] ^= 0xFF;
        bus.ReceiveBytes(frame);
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Contains(wrongAddress ? "address=2" : "CRC is invalid", error.Message);
        Assert.Contains($"RX={Convert.ToHexString(frame)}", error.Message);
        Assert.Contains(wrongAddress ? "CRC valid" : "calculated=", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task CancellationKeepsLoggedBytesAndDoesNotAssignAnOldPartialFrameToTheNextRequest(int receivedCount)
    {
        var oldFrame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, [2, 0x12, 0x34]);
        var nextFrame = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, [2, 0x56, 0x78]);
        using var bus = new AdcBus(new());
        using var cancellation = new CancellationTokenSource();
        var logged = new List<byte>();
        bus.FrameTransferred += (direction, bytes) => logged.AddRange(bytes);
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 2);
        var waiting = bus.WaitForResponseAsync(pending, cancellation.Token);
        if (receivedCount > 0)
            bus.ReceiveBytes(oldFrame[..receivedCount]);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(oldFrame[..receivedCount], logged);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var next = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 2);
        if (receivedCount > 0)
        {
            bus.ReceiveBytes(oldFrame[receivedCount..]);
            Assert.False(next.Completion.Task.IsCompleted);
        }
        bus.ReceiveBytes(nextFrame);
        Assert.Equal(nextFrame, await bus.WaitForResponseAsync(next, CancellationToken.None));
    }

    [Fact]
    public void Unsolicited8403NotificationRequiresACompleteValidFrame()
    {
        using var bus = new AdcBus(new());
        var notifications = new List<byte>();
        bus.ResultNotificationReceived += notifications.Add;
        byte[] notification = [1, 0x84, 3, 3, 1];
        var corrupt = (byte[])notification.Clone();
        corrupt[^1] ^= 0xFF;
        bus.ReceiveBytes(corrupt);
        bus.ReceiveBytes([1, 0x8C, 3, 4, 0xC1]);
        bus.ReceiveBytes(AdcRtuFrame.Build(1, (AdcFunctionCode)0x84, [2]));
        Assert.Empty(notifications);
        bus.ReceiveBytes(notification[..2]);
        Assert.Empty(notifications);
        bus.ReceiveBytes(notification[2..]);
        Assert.Equal(new byte[] { 1 }, notifications);
    }

    [Fact]
    public async Task RequestedReadRejectionIsNotAnUnsolicitedNotification()
    {
        using var bus = new AdcBus(new());
        var notifications = new List<byte>();
        bus.ResultNotificationReceived += notifications.Add;
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 28);
        bus.ReceiveBytes([1, 0x84, 3, 3, 1]);
        await Assert.ThrowsAsync<AdcResponseException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task LateRejectionOfACancelledQueryDoesNotBecomeAFasteningResult()
    {
        using var bus = new AdcBus(new());
        var notifications = new List<byte>();
        bus.ResultNotificationReceived += notifications.Add;
        using var cancellation = new CancellationTokenSource();
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 14);
        var response = bus.WaitForResponseAsync(pending, cancellation.Token);
        byte[] rejection = [1, 0x84, 3, 3, 1];
        bus.ReceiveBytes(rejection[..2]);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        bus.ReceiveBytes([.. rejection[2..], .. AutomaticFrame(11)]);
        Assert.Empty(notifications);
        Assert.Equal((ushort)11, (await bus.WaitForFasteningResultAsync(1, CancellationToken.None)).EventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticResultCanArriveBeforeOrAfterStartEchoInOneEvent(bool resultFirst)
    {
        var automatic = AutomaticFrame(7);
        var echo = AdcRtuFrame.Build(1, AdcFunctionCode.WriteSingleRegister, [0x0F, 0xA3, 0, 1]);
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.WriteSingleRegister);
        bus.ReceiveBytes(resultFirst ? [.. automatic, .. echo] : [.. echo, .. automatic]);
        Assert.Equal(echo, await bus.WaitForResponseAsync(pending, CancellationToken.None));
        var result = await bus.WaitForFasteningResultAsync(1, CancellationToken.None);
        Assert.Equal((ushort)7, result.EventCount);
        Assert.Equal(AdcEventStatus.FasteningOk, result.Status);
    }

    [Fact]
    public async Task AutomaticResultIsBufferedWithoutAnyPendingRequest()
    {
        using var bus = new AdcBus(new());
        var sends = 0;
        bus.FrameTransferred += (direction, bytes) =>
        {
            if (direction == AdcFrameDirection.Transmit)
                sends++;
        };
        var frame = AutomaticFrame(9);
        bus.ReceiveBytes(frame[..3]);
        bus.ReceiveBytes(frame[3..]);
        var result = await bus.WaitForFasteningResultAsync(1, CancellationToken.None);
        Assert.Equal((ushort)9, result.EventCount);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task AutomaticResultIsNotMistakenForStatusResponse()
    {
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 14);
        byte[] statusData = [14, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0];
        var status = AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, statusData);
        bus.ReceiveBytes([.. AutomaticFrame(10), .. status]);
        Assert.Equal(status, await bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Equal((ushort)10, (await bus.WaitForFasteningResultAsync(1, CancellationToken.None)).EventCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task StopRejectionBetweenAutomaticEventsKeepsFrameBoundaries(int chunkSize)
    {
        using var bus = new AdcBus(new());
        var first = AutomaticFrame(1);
        // Header-looking payload bytes must not restart frame collection.
        first[8] = 1;
        first[9] = 0x86;
        first[10] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(first.AsSpan(^2), AdcRtuFrame.CalculateCrc(first.AsSpan(0, first.Length - 2)));
        byte[] rejection = [1, 0x86, 3, 2, 0x61];
        byte[] incoming = [.. first, .. rejection, .. AutomaticFrame(2)];
        var pending = bus.BeginResponse(1, AdcFunctionCode.WriteSingleRegister);
        for (var offset = 0; offset < incoming.Length; offset += chunkSize)
            bus.ReceiveBytes(incoming[offset..Math.Min(incoming.Length, offset + chunkSize)]);
        var error = await Assert.ThrowsAsync<AdcResponseException>(
            () => bus.WaitForResponseAsync(pending, CancellationToken.None));
        Assert.Equal(3, error.ErrorCode);
        Assert.Contains("RX=0186030261", error.Message);
        Assert.Equal((ushort)1, (await bus.WaitForFasteningResultAsync(1, CancellationToken.None)).EventCount);
        Assert.Equal((ushort)2, (await bus.WaitForFasteningResultAsync(1, CancellationToken.None)).EventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFramesValidateAddressAndCrc(bool wrongAddress)
    {
        using var bus = new AdcBus(new());
        var frame = AutomaticFrame(1);
        if (wrongAddress)
        {
            frame[0] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(^2), AdcRtuFrame.CalculateCrc(frame.AsSpan(0, frame.Length - 2)));
        }
        else
            frame[^1] ^= 0xFF;
        bus.ReceiveBytes(frame);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => bus.WaitForFasteningResultAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task RawCaptureKeepsEchoAndLateBytesUntilDeadline()
    {
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(0, AdcFunctionCode.RequestDeviceInformation, capture: true);
        var capture = bus.WaitForResponseAsync(pending, CancellationToken.None, 100);
        byte[] echo = [0, 0x11, 0xC1, 0xBC];
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [2, 1, 0xFF]);
        bus.ReceiveBytes(echo);
        await Task.Delay(10);
        Assert.False(capture.IsCompleted);
        bus.ReceiveBytes([.. frame, 0xAB]);
        Assert.Equal([.. echo, .. frame, 0xAB], await capture);
    }

    [Fact]
    public async Task RawCaptureCancellationLeavesTheReceiverAvailableForTheNextResponse()
    {
        using var bus = new AdcBus(new());
        using var cancellation = new CancellationTokenSource();
        var pending = bus.BeginResponse(0, AdcFunctionCode.RequestDeviceInformation, capture: true);
        var capture = bus.WaitForResponseAsync(pending, cancellation.Token, 3000);
        byte[] echo = [0, 0x11, 0xC1, 0xBC];
        bus.ReceiveBytes(echo);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(echo, pending.Bytes);
        var next = bus.BeginResponse(0, AdcFunctionCode.RequestDeviceInformation);
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [2, 1, 0xFF]);
        bus.ReceiveBytes(frame);
        Assert.Equal(frame, await bus.WaitForResponseAsync(next, CancellationToken.None));
    }

    [Fact]
    public async Task ClosingTheBusReleasesPendingResponseAndAutomaticResultWaits()
    {
        using var bus = new AdcBus(new());
        var pending = bus.BeginResponse(1, AdcFunctionCode.ReadInputRegisters, 14);
        var response = bus.WaitForResponseAsync(pending, CancellationToken.None);
        var result = bus.WaitForFasteningResultAsync(1, CancellationToken.None);
        bus.Close();
        await Assert.ThrowsAsync<IOException>(() => response);
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => result);
    }

    private static byte[] AutomaticFrame(ushort eventCount)
    {
        var data = new byte[29];
        data[0] = 28;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), eventCount);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(25), (ushort)AdcEventStatus.FasteningOk);
        return AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, data);
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
