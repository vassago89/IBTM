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
    [InlineData(FasteningHead.Shooting, false, false)]
    [InlineData(FasteningHead.Pickup, false, false)]
    [InlineData(FasteningHead.Pickup, true, false)]
    [InlineData(FasteningHead.Shooting, true, false)]
    [InlineData(FasteningHead.Pickup, false, true)]
    [InlineData(FasteningHead.Shooting, false, false, true)]
    public async Task IoFasteningStartsBeforeDescentAndStopsAfterCompletionOrFeedFailure(
        FasteningHead selectedHead,
        bool stopDuringDescent,
        bool missingDownFeedback,
        bool loseTableUp = false)
    {
        var settings = new BoltFasteningSettings
        {
            SafeZ = 5,
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            PickupPosition = new() { X = 100, Y = 100, Z = 10 },
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var controllerSettings = new IoBoltHardwareSettings();
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings(), controllerSettings),
            new() { TimeoutMilliseconds = missingDownFeedback ? 100 : 2_000 })
        { AutoResponseEnabled = false };
        using var motion = Motion(settings.Motion, new());
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        using var pickup = new IoBoltHead(io, FasteningHead.Pickup, controllerSettings);
        using var shooting = new IoBoltHead(io, FasteningHead.Shooting, controllerSettings);
        var gantry = new BoltFasteningGantry(
            shooting, pickup, io, motion, settings,
            new CarrierReferenceSettings { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100 } });
        await gantry.MoveZAsync(settings.GetHead(selectedHead).FasteningZ);
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout { BoltPoints = [Bolt(1, selectedHead, 0, 0)] };
        var station = new BoltFasteningStation(
            gantry,
            work,
            new PickupBoltFeeder(io, new()),
            new ShootingBoltFeeder(io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, new());
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupTableUp, selectedHead != FasteningHead.Pickup),
            (InputIo.PickupTableDown, selectedHead == FasteningHead.Pickup),
            (InputIo.PickupHeadVacuumDetected, true),
            (InputIo.ShootingHeadVacuumDetected, true),
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
        var descending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        io.OutputChanged += (output, on) =>
        {
            if (!on && output == OutputIo.PickupHeadVacuumPump)
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
            PickupHead = HeadSettings(),
            ShootingHead = HeadSettings(),
        };
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
        IBoltHead pickup = useIo ? pickupIo : new AdcBoltHead(bus, new HantasSettings(), 1);
        var gantry = new BoltFasteningGantry(
            shooting, pickup, io, motion, settings,
            new CarrierReferenceSettings
            {
                UpperLeftLocatingPin = new(),
                LowerRightLocatingPin = new() { X = 100 },
            });
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout { BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)] };
        var units = new UnitSettings();
        var station = new BoltFasteningStation(
            gantry,
            work,
            new PickupBoltFeeder(io, new()),
            new ShootingBoltFeeder(io, new()),
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
        using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                io.SetInput(InputIo.PickupBoltFasten, true);
                io.SetInput(InputIo.PickupBoltFasten, false);
            }
            if (output == OutputIo.PickupHeadDown && !on && assembly.PickupBoltResults.ContainsKey(1))
                finish.Cancel();
        };
        await station.RunAsync(finish.Token);
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
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var layout = new PcbLayout
        {
            BoltPoints = [Bolt(1, FasteningHead.Pickup, 0, 0)],
        };
        var station = new BoltFasteningStation(
            gantry,
            work,
            new PickupBoltFeeder(io, new()),
            new ShootingBoltFeeder(io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, new());
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
            Motion = new() { HorizontalSpeed = 20_000, ZSpeed = 20_000 },
            SafeZ = 5,
            PickupPosition = new() { X = 100, Y = 50, Z = 10 },
            ShootingHead = HeadSettings(),
            PickupHead = HeadSettings(),
        };
        settings.ShootingHead.FasteningZ = 12;
        settings.PickupHead.FasteningZ = 16;
        var io = new VirtualIoService(
            Outputs(new BoltFasteningHardwareSettings(), new ConveyorHardwareSettings()), new());
        using var motion = new VirtualMotionService(settings.Motion, operationCancellation: new(), horizontalZ: () => settings.SafeZ);
        var bus = new VirtualAdcBus();
        var gantry = new BoltFasteningGantry(
            new AdcBoltHead(bus, new HantasSettings(), 2),
            new AdcBoltHead(bus, new HantasSettings(), 1),
            io, motion, settings,
            new() { UpperLeftLocatingPin = new(), LowerRightLocatingPin = new() { X = 100 } });
        var layout = new PcbLayout
        {
            BoltPoints = [
                Bolt(1, FasteningHead.Shooting, 20, 30),
                Bolt(2, FasteningHead.Pickup, 25, 30),
                new() { Number = 1, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Shooting, X = 40, Y = 30 },
                new() { Number = 2, HeatSink = HeatSinkSlot.HeatSink2, Head = FasteningHead.Pickup, X = 45, Y = 30 },
            ],
        };
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
        var station = new BoltFasteningStation(
            gantry, work, new PickupBoltFeeder(io, new()), new ShootingBoltFeeder(io, new()),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } },
            new() { PickupBoltFeeder = false, ShootingBoltFeeder = false });
        await ((IAdcBus)bus).SelectPresetAsync(2, 4);
        await ((IAdcBus)bus).SelectPresetAsync(1, 5);
        var presets = new List<(byte Head, ushort Preset)>();
        var starts = new List<(byte Head, double X, double Z)>();
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
            starts.Add((frame[0], position.X, position.Z));
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
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        motion.PositionChanged += (_, _, z) =>
        {
            if (motion.IsMovingHorizontal)
            {
                Assert.Equal(settings.SafeZ, z);
                Assert.True(gantry.IsHorizontalMoveAllowed);
                if (tableDescents > 0 && !work.Completed)
                    Assert.Equal(BoltCylinderState.Down, gantry.PickupTablePosition);
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((20d, 30d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
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
            Assert.Equal(new (byte, double, double)[] { (2, 20, 12), (2, 40, 12), (1, 25, 16), (1, 45, 16) }, starts);
            Assert.Equal(new (byte, ushort)[] { (2, 1), (1, 1) }, presets);
            Assert.Equal(2, pickups);
            Assert.Equal(1, tableDescents);
            Assert.All(work.Assemblies, assembly => Assert.Single(assembly.PickupBoltResults));
            Assert.True(await WaitUntilAsync(
                () => station.GetState() == BoltFasteningState.Waiting, TimeSpan.FromSeconds(2)));
            Assert.Equal((20d, 30d, 5d), (motion.GetPosition().X, motion.GetPosition().Y, motion.GetPosition().Z));
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
        var work = new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new());
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
        var station = new BoltFasteningStation(
            gantry,
            work,
            pickupFeeder,
            shootingFeeder,
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, new());

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
            new BoltFasteningWork(ConveyorStation.CreateBoltFastening(io), new()),
            new PickupBoltFeeder(io, feederSettings),
            new ShootingBoltFeeder(io, feederSettings),
            new RecipeManager(OpenMachineStore(), new()) { Current = { Pcb = layout } }, new());
        io.SetInput(InputIo.PickupHeadUp, true);
        io.SetInput(InputIo.ShootingHeadUp, true);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        motion.Initialize();
        await HomeAsync(motion, 20_000);
        await gantry.MoveToXYAsync(10, 10);
        if (head == FasteningHead.Pickup)
        {
            io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
            io.SetOutput(OutputIo.PickupHeadDown, true);
            io.SetInput(InputIo.PickupHeadUp, false);
            io.SetInput(InputIo.PickupHeadDown, true);
            await gantry.MoveZAsync(settings.PickupPosition.Z);
            io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        }
        else
        {
            await gantry.MoveZAsync(settings.ShootingHead.FasteningZ);
            io.SetOutput(OutputIo.ShootingEscapeForward, true);
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
            cancelled.Cancel();
            await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        }

        var stationChanges = 0;
        var gantryChanges = 0;
        station.Changed += () => Interlocked.Increment(ref stationChanges);
        gantry.Changed += () => Interlocked.Increment(ref gantryChanges);
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
        var run = station.RunAsync(stop.Token);
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
        Assert.True(stationChanges > 0);
        Assert.True(gantryChanges > 0); // Display listeners still receive head feedback.
        Assert.Equal(
            head == FasteningHead.Pickup ? settings.SafeZ : settings.ShootingHead.FasteningZ,
            motion.GetPosition().Z);
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

    public enum AdcResponseKind
    {
        Valid,
        InvalidCrc,
        WrongAddress,
        WriteResponse,
        ControllerError,
    }
}
