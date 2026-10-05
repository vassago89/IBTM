using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.Hantas;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class AdcProtocolTests
{
    [Fact]
    public async Task HCommOwnsReadWriteInfoAndAdcGraphRequests()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var read = bus.ReadRegistersAsync(0, AdcFunctionCode.ReadHoldingRegisters, 10, 1);
        Assert.Equal(AdcRtuFrame.Build(0, AdcFunctionCode.ReadHoldingRegisters, [0, 10, 0, 1]),
            await transport.NextRequestAsync());
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.ReadHoldingRegisters, [2, 0x12, 0x34]));
        Assert.Equal(new ushort[] { 0x1234 }, await read);

        var write = bus.WriteRegisterAsync(0, 4004, 2);
        var request = await transport.NextRequestAsync();
        Assert.Equal(AdcRtuFrame.Build(0, AdcFunctionCode.WriteSingleRegister, [0x0F, 0xA4, 0, 2]), request);
        transport.Receive(request);
        await write;

        var info = bus.ReadDeviceInformationAsync(0);
        Assert.Equal(AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, []), await transport.NextRequestAsync());
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.RequestDeviceInformation, [2, 0x12, 0x34]));
        Assert.Equal(new byte[] { 0x12, 0x34 }, await info);

        var graph = bus.RequestTorqueCurveAsync(0);
        Assert.Equal(AdcRtuFrame.Build(0, AdcFunctionCode.RequestTorqueCurve, [0]), await transport.NextRequestAsync());
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.RequestTorqueCurve, [4, 1, 1, 0xFF, 0xFE]);
        transport.Receive(frame[..3]);
        Assert.False(graph.IsCompleted);
        transport.Receive(frame[3..]);
        Assert.Equal(frame, await graph);
    }

    [Theory]
    [InlineData("018602C3A1", 2)]
    [InlineData("0184030301", 3)]
    [InlineData("018C0304C1", 3)]
    public async Task HCommReplaysEquipmentErrorsWithRequestAndRawBytes(string rx, byte code)
    {
        using var transport = new HCommTransportStub(1);
        using var bus = new AdcBus(transport.Communication, 1);
        var reading = bus.ReadRegistersAsync(1, AdcFunctionCode.ReadInputRegisters, 3200, 14);
        var tx = await transport.NextRequestAsync();
        transport.Receive(Convert.FromHexString(rx));
        var error = await Assert.ThrowsAsync<AdcResponseException>(() => reading);
        Assert.Equal(code, error.ErrorCode);
        Assert.Contains("request=Mor, address=3200, data=14", error.Message);
        Assert.Contains($"TX={Convert.ToHexString(tx)}", error.Message);
        Assert.Contains($"RX={rx}", error.Message);
    }

    [Fact]
    public async Task HCommStatusRejectsUnknownFeedbackAndAcceptsTheNextSample()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        transport.Receive(Convert.FromHexString("018C0304C1"));
        var rejected = await status;
        Assert.Null(rejected.Status);
        Assert.Contains("0x03", rejected.Rejection);
        status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters,
            [28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True((await status).Status?.Ready);
    }

    [Fact]
    public async Task HCommFirstDecodedStatusDoesNotRequireTheRawCallbackAttachedByThatReply()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication, 1);
        var status = bus.ReadControllerStatusAsync(1);
        await transport.NextRequestAsync();
        // HcSerial reports raw bytes before its first valid frame establishes the connection.
        // HComm attaches its raw callback during that connection notification.
        transport.Receive(AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters,
            [28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
            rawBeforeConnection: true);
        var response = await status;
        Assert.Null(response.Rejection);
        Assert.True(response.Status?.Ready);
    }

    [Fact]
    public async Task HCommKeepsGraphEventsSeparateFromStatusReplies()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        bus.Monitor.BeginTorqueCurveCapture();
        AdcTorqueCurve? curve = null;
        bus.Monitor.TorqueCurveReceived += received => curve = received;
        var status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        transport.Receive(GraphBlock(2, 1, [1, 0, 2, 1, 3, 250, 125, 120]));
        Assert.False(status.IsCompleted);
        Assert.Null(curve);
        // The remaining block arrives with a status reply in the same serial read.
        var last = GraphBlock(2, 2, [1000, 10, 20, 0, 0, 2, 0, -25, 50, 120]);
        var statusFrame = AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters,
            [28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        transport.Receive([.. last, .. statusFrame]);
        Assert.False(status.IsCompleted);
        transport.Receive([]);
        Assert.True((await status).Status?.Ready);
        Assert.NotNull(curve);
        Assert.Equal(new double[] { -0.25, 0.5, 1.2 }, curve.Torques);
        Assert.Equal(10, curve.SampleMilliseconds);
        Assert.Equal(250, curve.FasteningMilliseconds);
        Assert.Equal(1.25, curve.TargetTorque);
        Assert.Equal(1.2, curve.FinalTorque);
        Assert.Equal(2, curve.ScrewCount);
        Assert.Null(bus.Monitor.TorqueCurveError);

        // Also collect a complete unsolicited graph while no request is outstanding.
        bus.Monitor.BeginTorqueCurveCapture();
        curve = null;
        transport.Receive(GraphBlock(1, 1, [1, 0, 1, 1, 2, 10, 100, 90, 1000, 0, 0, 0, 0, 1, 0, 10, 90]));
        Assert.NotNull(curve);
        Assert.Equal(new double[] { 0.1, 0.9 }, curve.Torques);
    }

    private static byte[] GraphBlock(byte total, byte block, int[] words)
    {
        var data = new byte[3 + words.Length * 2];
        data[0] = checked((byte)(data.Length - 1));
        data[1] = total;
        data[2] = block;
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(3 + index * 2), unchecked((short)words[index]));
        return AdcRtuFrame.Build(0, AdcFunctionCode.RequestTorqueCurve, data);
    }

    [Fact]
    public async Task AdcGraphRequestDrainsEveryBlockBeforeTheNextCommand()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var graph = bus.RequestTorqueCurveAsync(0);
        await transport.NextRequestAsync();
        var first = GraphBlock(3, 1, [1, 0, 1, 1, 2, 10, 100, 90]);
        var middle = GraphBlock(3, 2, [1000, 0, 0, 0, 0, 1, 0, 10]);
        var last = GraphBlock(3, 3, [90]);
        transport.Receive(first);
        // Starting a new bolt during a refresh must still drain the old graph completely.
        bus.Monitor.BeginTorqueCurveCapture();
        var next = bus.WriteRegisterAsync(0, 4004, 1);
        transport.Receive(middle);
        Assert.False(graph.IsCompleted);
        Assert.False(next.IsCompleted);
        transport.Receive(last);
        Assert.Equal(first.Concat(middle).Concat(last).ToArray(), await graph);
        Assert.Null(bus.Monitor.TorqueCurveError);
        var write = await transport.NextRequestAsync();
        Assert.Equal((byte)AdcFunctionCode.WriteSingleRegister, write[1]);
        transport.Receive(write);
        await next;
    }

    [Fact]
    public async Task CompletedBoltRequestsAFreshCurveEvenWhenThePreviousValuesMatch()
    {
        using var bus = new AdcControllerStub { SuppressTorqueCurve = true };
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(true, CancellationToken.None);
        bus.Monitor.BeginTorqueCurveCapture();
        var controller = new BoltControllerData("Virtual", 1, 2, 250, 1, 1, 1000, 0, 0, 0, 2, 0, 0, 1, 0, null);
        var previous = new AdcTorqueCurve(System.Diagnostics.Stopwatch.GetTimestamp(), 5, [0, 1], 250, 1, 1, 2, 0);
        bus.Monitor.ReceiveTorqueCurve(previous);
        var waiting = bus.Monitor.ReadTorqueCurveAsync(controller, 1, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.GraphRequests == 2, TimeSpan.FromSeconds(1)));
        bus.Monitor.ReceiveTorqueCurve(previous);
        Assert.False(waiting.IsCompleted);
        var current = previous with { ReceivedAt = System.Diagnostics.Stopwatch.GetTimestamp(), Torques = [0, 0.5, 1] };
        bus.Monitor.ReceiveTorqueCurve(current);
        Assert.Same(current, await waiting);
    }

    [Fact]
    public void AdcGraphRejectsMissingBlocksAndDoesNotCombineDifferentBolts()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        bus.Monitor.BeginTorqueCurveCapture();
        AdcTorqueCurve? curve = null;
        bus.Monitor.TorqueCurveReceived += received => curve = received;
        var first = GraphBlock(2, 1, [1, 0, 1, 1, 2, 10, 100, 90]);
        var last = GraphBlock(2, 2, [1000, 0, 0, 0, 0, 1, 0, 10, 90]);
        transport.Receive(first);
        bus.Monitor.BeginTorqueCurveCapture();
        transport.Receive(last);
        Assert.Null(curve);
        Assert.Null(bus.Monitor.TorqueCurveError);
        transport.Receive(GraphBlock(3, 1, [1, 0, 1, 1, 2, 10, 100, 90]));
        transport.Receive(GraphBlock(3, 3, [10, 90]));
        Assert.NotNull(bus.Monitor.TorqueCurveError);
        transport.Receive(first);
        transport.Receive(last);
        Assert.NotNull(curve);
        Assert.Null(bus.Monitor.TorqueCurveError);

        bus.Monitor.BeginTorqueCurveCapture();
        curve = null;
        transport.Receive(GraphBlock(1, 1, [1, 0, 1, 1, 200, 1500, 100, 90, 1000, 0, 0, 0, 0, 1, 0, 10, 90]));
        Assert.Null(curve);
        Assert.NotNull(bus.Monitor.TorqueCurveError);
    }

    [Fact]
    public void AdcGraphUsesChannelTwoTorqueAndFullBufferTimeOffset()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        bus.Monitor.BeginTorqueCurveCapture();
        AdcTorqueCurve? curve = null;
        bus.Monitor.TorqueCurveReceived += received => curve = received;
        int[] words = [3, 1, 1, 1, 200, 1500, 100, 90, 1000, 0, 0, 0, 0, 1, 0,
            .. Enumerable.Repeat(1000, 200), .. Enumerable.Repeat(90, 200)];
        for (byte block = 0; block < 5; block++)
            transport.Receive(GraphBlock(5, (byte)(block + 1), words.Skip(block * 100).Take(100).ToArray()));
        Assert.NotNull(curve);
        Assert.Equal(200, curve.Torques.Length);
        Assert.All(curve.Torques, torque => Assert.Equal(0.9, torque));
        Assert.Equal(500, curve.StartMilliseconds);
    }

    [Fact]
    public async Task HCommStatusStillCompletesAfterIgnoredGraphOutsideCapture()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.RequestTorqueCurve, [2, 0, 0]));
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters,
            [28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True((await status).Status?.Ready);
        Assert.Null(bus.Monitor.TorqueCurveError);
    }

    [Fact]
    public async Task HCommCrcAndLengthFailuresCannotBecomeFeedback()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        var frame = AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters, [2, 0, 1]);
        frame[^1] ^= 0xFF;
        transport.Receive(frame);
        await Assert.ThrowsAsync<InvalidDataException>(() => status);
        status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        frame[^1] ^= 0xFF;
        transport.Receive(frame);
        var mismatch = await status;
        Assert.Null(mismatch.Status);
        Assert.Contains("reply mismatch", mismatch.Rejection);
    }

    [Fact]
    public async Task HCommForeignSlaveAndWrongWriteEchoCannotCompleteTheRequest()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var read = bus.ReadRegistersAsync(0, AdcFunctionCode.ReadInputRegisters, 3200, 1);
        await transport.NextRequestAsync();
        transport.Receive(AdcRtuFrame.Build(1, AdcFunctionCode.ReadInputRegisters, [2, 0, 1]));
        await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() => read);
        var write = bus.WriteRegisterAsync(0, 4004, 1);
        await transport.NextRequestAsync();
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.WriteSingleRegister, [0x0F, 0xA4, 0, 2]));
        await Assert.ThrowsAsync<AdcUnexpectedResponseException>(() => write);
    }

    [Fact]
    public async Task HCommRawCaptureRetainsUnparsedBytesWithoutTurningThemIntoFeedback()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var capture = bus.CaptureDeviceInformationAsync(0, 50);
        await transport.NextRequestAsync();
        byte[] raw = [0, 0x11, 0, 0, 0]; // Deliberately invalid CRC, retained by the raw diagnostic.
        transport.Receive(raw);
        Assert.Equal(raw, await capture);
        Assert.Null(bus.Monitor.Sample?.Status);
    }

    [Fact]
    public async Task HCommCancellationDrainsTheRequestBeforeAnotherCommand()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        using var cancellation = new CancellationTokenSource();
        var first = bus.ReadRegistersAsync(0, AdcFunctionCode.ReadInputRegisters, 3200, 1, cancellation.Token);
        await transport.NextRequestAsync();
        cancellation.Cancel();
        var next = bus.ReadRegistersAsync(0, AdcFunctionCode.ReadInputRegisters, 3303, 1);
        Assert.False(first.IsCompleted);
        Assert.False(next.IsCompleted);
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters, [2, 0, 5]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters, [0x0C, 0xE7, 0, 1]),
            await transport.NextRequestAsync());
        transport.Receive(AdcRtuFrame.Build(0, AdcFunctionCode.ReadInputRegisters, [2, 0, 9]));
        Assert.Equal(new ushort[] { 9 }, await next);
    }

    [Fact]
    public async Task HCommTimeoutAndCloseReleasePendingCalls()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        var status = bus.ReadControllerStatusAsync(0);
        await transport.NextRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => status);
        var read = bus.ReadDeviceInformationAsync(0);
        await transport.NextRequestAsync();
        bus.Close();
        await Assert.ThrowsAsync<IOException>(() => read);
        await Assert.ThrowsAsync<IOException>(() => bus.ReadRegistersAsync(
            0, AdcFunctionCode.ReadInputRegisters, (ushort)AdcStatusRegister.Preset, 7));
        Assert.False(bus.IsOpen);
        Assert.Null(bus.Monitor.Sample?.Status);
    }

    [Fact]
    public void HCommConnectionCannotChangePortBaudOrSlaveWhileOpen()
    {
        using var transport = new HCommTransportStub();
        using var bus = new AdcBus(transport.Communication);
        bus.Open(string.Empty, 0, 0);
        Assert.Throws<InvalidOperationException>(() => bus.Open("COM3", 0, 0));
        Assert.Throws<InvalidOperationException>(() => bus.Open(string.Empty, 19200, 0));
        Assert.Throws<InvalidOperationException>(() => bus.Open(string.Empty, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => bus.Open(string.Empty, 0, 16));
    }

    [Fact]
    public async Task TorqueMonitoringRefreshesAndOnlyReturnsTheCurrentMatchingCurve()
    {
        using var bus = new AdcControllerStub();
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(true, CancellationToken.None);
        Assert.Equal(new (ushort, ushort)[] { (4101, 1), (4102, 0), (4103, 1), (4104, 1) }, bus.RegisterWrites);
        Assert.Equal(1, bus.GraphRequests);
        Assert.Null(bus.Monitor.TorqueCurveError);
        var controller = new BoltControllerData("Virtual", 1, 2, 250, 1, 1, 1000, 0, 0, 0, 2, 0, 0, 1, 0, null);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();
        bus.Monitor.ReceiveTorqueCurve(new(after - 1, 5, [0, 1], 250, 1, 1, 2, 0));
        var waiting = bus.Monitor.ReadTorqueCurveAsync(controller, 1, CancellationToken.None);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(() => bus.GraphRequests >= 2, TimeSpan.FromSeconds(1)));
        bus.Monitor.ReceiveTorqueCurve(new(System.Diagnostics.Stopwatch.GetTimestamp(), 5, [0, 1], 250, 1, 1, 1, 0));
        Assert.False(waiting.IsCompleted);
        var expected = new AdcTorqueCurve(System.Diagnostics.Stopwatch.GetTimestamp(), 5, [0, 0.5, 1], 250, 1, 1, 2, 0);
        bus.Monitor.ReceiveTorqueCurve(expected);
        Assert.Same(expected, await waiting);
        await Task.Delay(5500);
        var requests = bus.GraphRequests;
        await bus.Monitor.SetTorqueCurveMonitoringAsync(false, CancellationToken.None);
        Assert.False(bus.Monitor.IsTorqueCurveMonitoringRequested);
        Assert.Equal(requests, bus.GraphRequests);
        Assert.True(requests >= 2);
        Assert.Equal(4, bus.RegisterWrites.Count);
        Assert.Null(bus.Monitor.TorqueCurveError);
    }

    [Fact]
    public async Task TorqueGraphRefreshFailureKeepsStatusUnknown()
    {
        using var bus = new AdcControllerStub();
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(true, CancellationToken.None);
        var failure = new IOException("Graph connection lost.");
        bus.GraphRequestFailure = failure;
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => ReferenceEquals(bus.Monitor.Sample?.Error, failure), TimeSpan.FromSeconds(7)));
        Assert.Null(bus.Monitor.Sample!.Status);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(false, CancellationToken.None);
        Assert.Equal(4, bus.RegisterWrites.Count);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var status = await bus.Monitor.WaitForSampleAsync(System.Diagnostics.Stopwatch.GetTimestamp(), timeout.Token);
        Assert.True(status.Ready);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task TorqueRequestPreservesOtherFailuresWithoutRegisterCleanup(int failureKind)
    {
        Exception failure = failureKind switch
        {
            0 => new IOException("Port disconnected."),
            _ => new TimeoutException("Graph reply missing."),
        };
        using var bus = new AdcControllerStub { GraphRequestFailure = failure };
        bus.Open("Virtual", 115200);
        await bus.Monitor.StartAsync(1, CancellationToken.None);
        Assert.Same(failure, await Record.ExceptionAsync(
            () => bus.Monitor.SetTorqueCurveMonitoringAsync(true, CancellationToken.None)));
        Assert.Null(bus.Monitor.TorqueCurveError);
        Assert.True(bus.Monitor.IsTorqueCurveMonitoringRequested);
        await bus.Monitor.SetTorqueCurveMonitoringAsync(false, CancellationToken.None);
        Assert.Equal(4, bus.RegisterWrites.Count);
        Assert.Equal(1, bus.GraphRequests);
        Assert.False(bus.Monitor.IsTorqueCurveMonitoringRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticStopAttemptsOffWhenPendingQueryFailsAndCloseRetainsErrors(bool failStop)
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        var status = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryError = new IOException("Result query failed.");
        using var bus = new AdcControllerStub
        {
            SuppressCompletion = true,
            StatusReadBarrier = status.Task,
            NextResultReadFailure = queryError,
        };
        bus.BindIo(io, FasteningHead.Pickup);
        using var otherBus = new VirtualAdcBus();
        using var diagnostics = new AdcProtocolViewModel(
            bus, otherBus, io, services.GetRequiredService<MachineSettings>().Hantas,
            machine, services.GetRequiredService<MachineState>());
        var context = new VirtualTestSupport.PausedSynchronizationContext();
        Task? reading = null;
        Task? stopping = null;
        try
        {
            diagnostics.SelectedPort = bus.PortName;
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            await VirtualTestSupport.WaitUntilAsync(() => bus.StatusReads > 0);
            io.SetOutput(OutputIo.PickupBoltStart, true);
            if (failStop)
                bus.StopWriteFailure = new IOException("START OFF failed.");
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                reading = diagnostics.ReadResultCommand.ExecuteAsync(null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            status.SetResult();
            await VirtualTestSupport.WaitUntilAsync(() => context.HasPending);
            stopping = diagnostics.StopCommand.ExecuteAsync(null);
            var closing = diagnostics.TryCloseAsync();
            context.Release();

            Assert.Same(queryError, await Assert.ThrowsAsync<IOException>(() => reading));
            var failure = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, bus.StopWrites);
            Assert.Contains(queryError.Message, diagnostics.CloseError);
            if (failStop)
            {
                var errors = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions;
                Assert.Contains(queryError, errors);
                Assert.Contains(bus.StopWriteFailure!, errors);
                Assert.Contains(bus.StopWriteFailure!.Message, diagnostics.CloseError);
            }
            else
            {
                Assert.Same(queryError, failure);
                Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            }
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            status.TrySetResult();
            context.Release();
            if (reading is not null)
                await Record.ExceptionAsync(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
            if (stopping is not null)
                await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(2)));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InvalidDiagnosticSlaveDoesNotOpenPortAndCanBeCorrected()
    {
        await using var services = MachineTestSupport.CreateDiagnosticServices();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        using var pickup = new VirtualAdcBus();
        using var shooting = new VirtualAdcBus();
        using var diagnostics = new AdcProtocolViewModel(
            pickup, shooting, services.GetRequiredService<IIoService>(),
            services.GetRequiredService<MachineSettings>().Hantas, machine,
            services.GetRequiredService<MachineState>());
        try
        {
            diagnostics.SelectedPort = "Virtual";
            diagnostics.SlaveText = "256";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.False(pickup.IsOpen);
            Assert.Equal("Slave must be 0–15.", diagnostics.ConnectionStatus);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

            diagnostics.SlaveText = "1";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.True(pickup.IsOpen);
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Contains(" = ", diagnostics.RegisterResult);
            var transmissions = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
            pickup.FrameTransferred += (direction, frame) =>
            {
                if (direction == AdcFrameDirection.Transmit)
                    transmissions.Enqueue(frame);
            };
            diagnostics.RegisterAccess = AdcFunctionCode.RequestTorqueCurve;
            diagnostics.AddressText = "";
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Contains(transmissions, frame => frame.SequenceEqual(new byte[] { 1, 0xC8, 0, 0x77, 0xC0 }));
            Assert.StartsWith("01C802", diagnostics.RegisterResult);
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.False(pickup.IsOpen);
        }
        finally
        {
            await diagnostics.ShutdownAsync();
            await machine.ShutdownAsync();
        }
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
            .AddSingleton(VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
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
        Assert.Equal(1_000, settings.ResponseTimeoutMilliseconds);
        Assert.Equal(200, settings.PresetSettleMilliseconds);
        Assert.Equal(3, settings.ReadAttempts);
        settings.ShootingPortName = "COM5";
        settings.ShootingBaudRate = 38400;
        settings.ShootingSlaveAddress = 2;
        settings.StatusPollMilliseconds = 75;
        settings.ResponseTimeoutMilliseconds = 250;
        settings.PresetSettleMilliseconds = 400;
        settings.ReadAttempts = 2;

        var reloaded = JsonSerializer.Deserialize<HantasSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal("COM4", reloaded.PickupPortName);
        Assert.Equal(19200, reloaded.PickupBaudRate);
        Assert.Equal("COM5", reloaded.ShootingPortName);
        Assert.Equal(38400, reloaded.ShootingBaudRate);
        Assert.Equal(reloaded.PickupSlaveAddress, reloaded.ShootingSlaveAddress);
        Assert.Equal(75, reloaded.StatusPollMilliseconds);
        Assert.Equal(250, reloaded.ResponseTimeoutMilliseconds);
        Assert.Equal(400, reloaded.PresetSettleMilliseconds);
        settings.PresetSettleMilliseconds = 0;
        Assert.Equal(0, JsonSerializer.Deserialize<HantasSettings>(JsonSerializer.Serialize(settings))!.PresetSettleMilliseconds);
        Assert.Equal(2, reloaded.ReadAttempts);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.StatusPollMilliseconds = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.ReadAttempts = 0);
    }

}
